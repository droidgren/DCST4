using DCST4.Core.Capability;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>The capability matrix as a test: which SOP classes and transfer syntaxes the system under test accepts.</summary>
    public sealed class NegotiationProbeTest : ITestCase
    {
        private readonly ProbeOptions _options;

        /// <param name="options">What to propose, or null for everything in <see cref="CapabilityCatalog"/>.</param>
        public NegotiationProbeTest(ProbeOptions options = null)
        {
            _options = options;
        }

        public string Id => "negotiation-probe";
        public string Name => "Negotiation probe (capability matrix)";
        public TestCategory Category => TestCategory.Capability;
        public string Description => "Proposes the SOP classes and transfer syntaxes DCST4 knows and records which the destination accepts. Fills the Capabilities tab, and the store tests use it to choose what to send in. Stores nothing.";
        public string Group => TestGroups.Association;
        public bool StoresObjects => false;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var outcome = await NegotiationProbe.RunAsync(context.Profile, _options, token).ConfigureAwait(false);
            var matrix = outcome.Matrix;
            context.Session.Matrix = matrix;

            var result = new TestResult
            {
                CapabilityMatrix = matrix,
                NegotiatedContexts = outcome.Contexts,
                Timeline = outcome.Timeline,
                StatusCodes = outcome.StatusCodes,
            };

            var cells = matrix.AllCells().Select(c => c.Item2).ToList();
            var accepted = cells.Count(c => c.Result == CapabilityResult.Accepted);
            var irregular = cells.Count(c => c.Result == CapabilityResult.NotAnswered || c.Result == CapabilityResult.AcceptedOtherSyntax);

            result.Details["combinations"] = Text(cells.Count);
            result.Details["accepted"] = Text(accepted);
            result.Details["associations"] = Text(matrix.Associations.Count);
            if (matrix.RemoteImplementationVersionName != null) result.Details["remoteImplementationVersionName"] = matrix.RemoteImplementationVersionName;
            if (matrix.RemoteMaxPduLength.HasValue) result.Details["remoteMaxPduLength"] = matrix.RemoteMaxPduLength.Value.ToString(CultureInfo.InvariantCulture);
            if (matrix.RemoteImplementationClassUid != null) result.Uids["remoteImplementationClassUid"] = matrix.RemoteImplementationClassUid;

            var endings = matrix.Associations.Where(a => a.Ending != AssociationEnding.Released && a.Ending != AssociationEnding.NotStarted).ToList();
            var target = $"{context.Profile.Endpoint.Host}:{context.Profile.Endpoint.Port}";
            var summary = $"{accepted} of {cells.Count} combinations accepted, in {matrix.Associations.Count} associations.";

            if (outcome.Cancelled)
            {
                Set(result, TestResultClass.INDETERMINATE, "The probe was cancelled before it finished. " + summary);
            }
            else if (endings.Any(a => a.Ending == AssociationEnding.LocalProblem))
            {
                Set(result, TestResultClass.TOOL_ERROR, $"DCST4 could not set up the connection, which is a local problem and not a fault of {target}: {endings[0].Detail}");
            }
            else if (endings.Any(a => a.Ending == AssociationEnding.TimedOut))
            {
                Set(result, TestResultClass.TIMEOUT, $"The probe of {target} timed out: {endings.First(a => a.Ending == AssociationEnding.TimedOut).Detail} {summary}");
            }
            else if (endings.Count > 0)
            {
                Set(result, TestResultClass.FAIL, $"{Describe(endings)} {summary}");
            }
            else if (accepted == 0)
            {
                Set(result, TestResultClass.NOT_SUPPORTED, $"{target} released every association and accepted none of the {cells.Count} combinations.");
            }
            else if (irregular > 0)
            {
                Set(result, TestResultClass.FAIL, $"{irregular} contexts were left unanswered or accepted with another transfer syntax than proposed. {summary}");
            }
            else
            {
                Set(result, TestResultClass.PASS, $"{summary} All were released.");
            }

            return result;
        }

        private static string Describe(IReadOnlyList<ProbeAssociationRecord> endings)
        {
            var parts = endings.Select(a => a.Rejection != null
                ? $"association {a.Index} was rejected ({a.Rejection.Result}, source {a.Rejection.Source}, reason {a.Rejection.Reason})"
                : $"association {a.Index} ended as {a.Ending}: {a.Detail}");
            return string.Join("; ", parts) + ".";
        }

        private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static void Set(TestResult result, TestResultClass resultClass, string explanation)
        {
            result.Result = resultClass;
            result.Explanation = explanation;
        }
    }
}
