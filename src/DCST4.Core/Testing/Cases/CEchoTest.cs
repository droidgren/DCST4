using DCST4.Core.Network;
using FellowOakDicom;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>Does the system under test answer C-ECHO? Built on the same service as the Echo button.</summary>
    public sealed class CEchoTest : ITestCase
    {
        private const string MessagePrefix = "Echo failed: ";

        public string Id => "c-echo";
        public string Name => "C-ECHO (Verification)";
        public TestCategory Category => TestCategory.Capability;
        public string Description => "Sends C-ECHO and checks that the destination answers Success. Stores nothing.";
        public string Group => TestGroups.Association;
        public bool StoresObjects => false;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var endpoint = context.Profile.Endpoint;
            var timeline = new AssociationTimeline();
            var echo = await EchoService.EchoAsync(endpoint, context.Profile.Network, token, timeline).ConfigureAwait(false);

            var result = new TestResult
            {
                NegotiatedContexts = timeline.Negotiation,
                Timeline = timeline.Events,
            };

            result.Uids["sopClassUid"] = DicomUID.Verification.UID;
            var accepted = result.NegotiatedContexts.FirstOrDefault(c => c.AcceptedTransferSyntaxUid != null);
            if (accepted != null) result.Uids["acceptedTransferSyntaxUid"] = accepted.AcceptedTransferSyntaxUid;

            if (echo.Failure != ConnectivityFailure.None) result.Details["failure"] = echo.Failure.ToString();
            if (echo.StatusCode.HasValue)
            {
                result.StatusCodes.Add(new DimseStatusRecord
                {
                    Operation = "C-ECHO",
                    Code = AssociationTimeline.FormatStatus(echo.StatusCode.Value),
                    Description = StoreStatus.Describe(echo.StatusCode.Value),
                });
            }

            Classify(result, echo, $"{endpoint.Host}:{endpoint.Port}");
            return result;
        }

        private static void Classify(TestResult result, ConnectivityResult echo, string target)
        {
            // echo.Message is not used as it stands: on success it holds the elapsed time, which differs between runs.
            var reason = Reason(echo.Message);
            var status = result.StatusCodes.FirstOrDefault();

            if (echo.Outcome == ConnectivityOutcome.Cancelled)
            {
                Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
            }
            else if (echo.Outcome == ConnectivityOutcome.Success)
            {
                Set(result, TestResultClass.PASS, $"{target} answered C-ECHO with status {status?.Code} ({status?.Description}).");
            }
            else if (echo.Failure == ConnectivityFailure.Timeout)
            {
                Set(result, TestResultClass.TIMEOUT, $"C-ECHO to {target} timed out: {reason}");
            }
            else if (echo.Failure == ConnectivityFailure.ContextNotAccepted)
            {
                Set(result, TestResultClass.NOT_SUPPORTED, $"{target} accepted the association but not the Verification SOP Class, so no C-ECHO was sent.");
            }
            else if (echo.Failure == ConnectivityFailure.LocalConfiguration)
            {
                Set(result, TestResultClass.TOOL_ERROR, $"DCST4 could not set up the connection, which is a local problem and not a fault of {target}: {reason}");
            }
            else if (echo.Failure == ConnectivityFailure.DimseStatus)
            {
                Set(result, TestResultClass.FAIL, $"{target} answered C-ECHO with status {status?.Code} ({status?.Description}).");
            }
            else
            {
                Set(result, TestResultClass.FAIL, $"C-ECHO to {target} failed: {reason}");
            }
        }

        private static void Set(TestResult result, TestResultClass resultClass, string explanation)
        {
            result.Result = resultClass;
            result.Explanation = explanation;
        }

        private static string Reason(string message) =>
            message.StartsWith(MessagePrefix, System.StringComparison.Ordinal) ? message.Substring(MessagePrefix.Length) : message;
    }
}
