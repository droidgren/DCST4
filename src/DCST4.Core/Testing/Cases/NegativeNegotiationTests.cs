using DCST4.Core.Network;
using DCST4.Core.TestData;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>One association attempt, kept with its timeline.</summary>
    internal sealed class NegotiationAttempt
    {
        private NegotiationAttempt(ConnectivityResult echo, AssociationTimeline timeline)
        {
            Echo = echo;
            Timeline = timeline;
        }

        public ConnectivityResult Echo { get; }
        public AssociationTimeline Timeline { get; }

        public static async Task<NegotiationAttempt> RunAsync(DicomEndpoint endpoint, NetworkOptions network, CancellationToken token, Action<IDicomClient, DicomCEchoRequest> configure = null)
        {
            var timeline = new AssociationTimeline();
            var echo = await EchoService.EchoAsync(endpoint, network, token, timeline, configure).ConfigureAwait(false);
            return new NegotiationAttempt(echo, timeline);
        }
    }

    /// <summary>Common ground of the tests where the expected answer is a refusal.</summary>
    public abstract class NegativeNegotiationTest : ITestCase
    {
        protected const string UnknownAe = "DCST4NOSUCHAE";
        private const string AlternativeUnknownAe = "DCST4NOSUCHAE2";

        public abstract string Id { get; }
        public abstract string Name { get; }
        public TestCategory Category => TestCategory.Capability;
        public abstract string Description { get; }
        public string Group => TestGroups.Association;
        public bool StoresObjects => false;
        public abstract Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token);

        /// <summary>An AE title the target should not know, and never the one the profile uses.</summary>
        protected static string Unknown(string ownAe) => string.Equals(ownAe, UnknownAe, StringComparison.OrdinalIgnoreCase) ? AlternativeUnknownAe : UnknownAe;

        protected static string Target(TestContext context) => $"{context.Profile.Endpoint.Host}:{context.Profile.Endpoint.Port}";

        protected static void Set(TestResult result, TestResultClass resultClass, string explanation)
        {
            result.Result = resultClass;
            result.Explanation = explanation;
        }

        private protected static void Record(TestResult result, NegotiationAttempt attempt)
        {
            result.NegotiatedContexts = attempt.Timeline.Negotiation;
            result.Timeline = attempt.Timeline.Events;
            var rejection = attempt.Timeline.Rejection;
            if (rejection != null)
            {
                result.Details["rejectResult"] = rejection.Result;
                result.Details["rejectSource"] = rejection.Source;
                result.Details["rejectReason"] = rejection.Reason;
            }
            if (attempt.Echo.Failure != ConnectivityFailure.None) result.Details["failure"] = attempt.Echo.Failure.ToString();
            if (attempt.Echo.StatusCode.HasValue)
            {
                result.StatusCodes.Add(new DimseStatusRecord
                {
                    Operation = "C-ECHO",
                    Code = AssociationTimeline.FormatStatus(attempt.Echo.StatusCode.Value),
                    Description = StoreStatus.Describe(attempt.Echo.StatusCode.Value),
                });
            }
        }

        /// <summary>
        /// Handles the endings that say nothing about the negotiation itself. Returns false when the attempt reached the SCP's
        /// verdict and the caller has to classify it.
        /// </summary>
        private protected static bool SetIfNoVerdict(TestResult result, NegotiationAttempt attempt, string target)
        {
            var echo = attempt.Echo;
            if (echo.Outcome == ConnectivityOutcome.Cancelled)
            {
                Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
                return true;
            }
            if (echo.Failure == ConnectivityFailure.LocalConfiguration)
            {
                Set(result, TestResultClass.TOOL_ERROR, $"DCST4 could not set up the connection, which is a local problem and not a fault of {target}: {echo.Message}");
                return true;
            }
            if (echo.Failure == ConnectivityFailure.Timeout)
            {
                Set(result, TestResultClass.TIMEOUT, $"The association attempt to {target} timed out: {echo.Message}");
                return true;
            }
            if (attempt.Timeline.Rejection == null && !attempt.Timeline.Accepted)
            {
                Set(result, TestResultClass.FAIL, $"{target} broke off instead of answering with A-ASSOCIATE-RJ: {echo.Message}");
                return true;
            }
            return false;
        }
    }

    /// <summary>A wrong AE title should be refused. An SCP that accepts it is a finding, as anyone could then talk to it.</summary>
    public abstract class WrongAeTitleTest : NegativeNegotiationTest
    {
        /// <summary>What was changed, as it should read in a sentence: "an unknown calling AE title".</summary>
        protected abstract string Change { get; }
        protected abstract string Finding { get; }
        protected abstract DicomEndpoint WithWrongAe(DicomEndpoint endpoint);

        public override async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var result = new TestResult();
            var target = Target(context);

            // Without this, a target that refuses everyone would look as if it had checked the AE title.
            var baseline = await NegotiationAttempt.RunAsync(context.Profile.Endpoint, context.Profile.Network, token).ConfigureAwait(false);
            if (!baseline.Timeline.Accepted)
            {
                Record(result, baseline);
                if (baseline.Echo.Failure == ConnectivityFailure.LocalConfiguration || baseline.Echo.Outcome == ConnectivityOutcome.Cancelled)
                {
                    SetIfNoVerdict(result, baseline, target);
                }
                else
                {
                    Set(result, TestResultClass.INDETERMINATE, $"{target} did not accept an association with the profile's own AE titles, so a refusal of {Change} would prove nothing: {baseline.Echo.Message}");
                }
                return result;
            }

            var endpoint = WithWrongAe(context.Profile.Endpoint.Trimmed());
            var attempt = await NegotiationAttempt.RunAsync(endpoint, context.Profile.Network, token).ConfigureAwait(false);
            Record(result, attempt);
            result.Details["callingAe"] = endpoint.CallingAe;
            result.Details["calledAe"] = endpoint.CalledAe;

            if (SetIfNoVerdict(result, attempt, target)) return result;

            var rejection = attempt.Timeline.Rejection;
            if (rejection != null)
            {
                Set(result, TestResultClass.EXPECTED_FAILURE, $"{target} rejected {Change}: {rejection}.");
            }
            else
            {
                result.Details["finding"] = Finding;
                Set(result, TestResultClass.FAIL, $"Finding: {target} accepted an association with {Change}.");
            }
            return result;
        }
    }

    public sealed class UnknownCallingAeTest : WrongAeTitleTest
    {
        public override string Id => "negotiation-unknown-calling-ae";
        public override string Name => "Unknown calling AE title";
        public override string Description => "Opens an association with a calling AE title the destination should not know. A refusal passes. Accepting it is a finding, since anyone could then connect. Stores nothing.";
        protected override string Change => "an unknown calling AE title";
        protected override string Finding => "accepted-unknown-calling-ae";

        protected override DicomEndpoint WithWrongAe(DicomEndpoint endpoint)
        {
            endpoint.CallingAe = Unknown(endpoint.CallingAe);
            return endpoint;
        }
    }

    public sealed class WrongCalledAeTest : WrongAeTitleTest
    {
        public override string Id => "negotiation-wrong-called-ae";
        public override string Name => "Wrong called AE title";
        public override string Description => "Opens an association to a called AE title that is not the destination's. A refusal passes. Accepting it is a finding. Stores nothing.";
        protected override string Change => "a wrong called AE title";
        protected override string Finding => "accepted-wrong-called-ae";

        protected override DicomEndpoint WithWrongAe(DicomEndpoint endpoint)
        {
            endpoint.CalledAe = Unknown(endpoint.CalledAe);
            return endpoint;
        }
    }

    /// <summary>
    /// An association proposing only an abstract syntax that cannot exist, under our own UID root. Both refusing the association
    /// and accepting it with every context rejected are allowed by the standard, and which of them the SCP does is recorded.
    /// </summary>
    public sealed class UnsupportedContextsTest : NegativeNegotiationTest
    {
        private const string UidContext = "negotiation/unsupported-contexts";

        public override string Id => "negotiation-unsupported-contexts";
        public override string Name => "Only unsupported presentation contexts";
        public override string Description => "Proposes only a SOP class that cannot exist. Refusing the association and accepting it with every context rejected both pass, and which one the destination did is recorded. Stores nothing.";

        public override async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var result = new TestResult();
            var target = Target(context);
            var abstractSyntax = DicomUID.Parse(new DeterministicUidSource(context.Profile.UidRoot, UidContext).Uid("abstract-syntax"));
            result.Uids["abstractSyntaxUid"] = abstractSyntax.UID;

            var attempt = await NegotiationAttempt.RunAsync(context.Profile.Endpoint, context.Profile.Network, token, (client, request) =>
            {
                // A context set on the request replaces the Verification context fo-dicom would otherwise add, so this is all that is proposed.
                request.PresentationContext = new DicomPresentationContext(0, abstractSyntax);
                request.PresentationContext.AddTransferSyntax(DicomTransferSyntax.ImplicitVRLittleEndian);
                // fo-dicom would send on that context even if it was refused. Without it there is nothing to send on, and nothing goes out.
                client.AssociationAccepted += (s, e) => request.PresentationContext = null;
            }).ConfigureAwait(false);

            Record(result, attempt);
            if (SetIfNoVerdict(result, attempt, target)) return result;

            var rejection = attempt.Timeline.Rejection;
            if (rejection != null)
            {
                result.Details["association"] = "rejected";
                Set(result, TestResultClass.EXPECTED_FAILURE, $"{target} rejected the association: {rejection}.");
                return result;
            }

            if (result.NegotiatedContexts.Any(c => c.AcceptedTransferSyntaxUid != null))
            {
                result.Details["association"] = "accepted-with-context-accepted";
                result.Details["finding"] = "accepted-unknown-abstract-syntax";
                Set(result, TestResultClass.FAIL, $"Finding: {target} accepted a presentation context for an abstract syntax that does not exist.");
                return result;
            }

            result.Details["association"] = "accepted-all-contexts-rejected";
            Set(result, TestResultClass.EXPECTED_FAILURE, $"{target} accepted the association and rejected its only presentation context ({result.NegotiatedContexts.Single().Result}).");
            return result;
        }
    }
}
