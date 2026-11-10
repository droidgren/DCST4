using DCST4.Core.TestData;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing
{
    /// <summary>Runs a set of tests against one target profile. One test going wrong never stops the others.</summary>
    public sealed class TestRunner
    {
        private const string CertificatePlaceholder = "[removed]";

        private static readonly ILogger Logger = Log.ForContext<TestRunner>();

        /// <summary>Raised on a background thread.</summary>
        public event Action<TestProgress> Progress;

        public async Task<TestRunReport> RunAsync(TargetProfile profile, IReadOnlyList<ITestCase> tests, CancellationToken token, TestPatientNaming patientNaming = TestPatientNaming.RegistrySwedishName, TestRunOptions options = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (tests == null) throw new ArgumentNullException(nameof(tests));

            var trimmed = profile.Trimmed();
            var session = new TestRunSession(options);
            var context = new TestContext(trimmed, patientNaming, session);
            var report = new TestRunReport
            {
                ToolVersion = ProductInfo.Version,
                StartedUtc = DateTime.UtcNow,
                Profile = ResultProfile.From(trimmed),
            };
            var clock = Stopwatch.StartNew();
            Logger.Information("Test run of {count} tests against {host}:{port} started", tests.Count, trimmed.Endpoint.Host, trimmed.Endpoint.Port);

            for (var i = 0; i < tests.Count; i++)
            {
                var test = tests[i];
                Raise(new TestProgress { Stage = TestProgressStage.Started, Index = i, Total = tests.Count, Test = test });
                var position = i;
                context.Reporter = activity => Raise(new TestProgress { Stage = TestProgressStage.Activity, Index = position, Total = tests.Count, Test = test, Activity = activity });

                var startedUtc = DateTime.UtcNow;
                var testClock = Stopwatch.StartNew();
                var result = token.IsCancellationRequested
                    ? Make(TestResultClass.INDETERMINATE, "The run was cancelled before this test started.")
                    : await ExecuteOneAsync(test, context, token).ConfigureAwait(false);

                result.TestId = test.Id;
                result.Name = test.Name;
                result.Category = test.Category;
                result.StartedUtc = startedUtc;
                result.EndedUtc = DateTime.UtcNow;
                result.DurationMs = testClock.ElapsedMilliseconds;
                Redact(result, trimmed.Endpoint.TlsClientCertThumbprint);
                Redact(result, trimmed.Endpoint.TlsExpiredClientCertThumbprint);

                context.Reporter = null;
                report.Results.Add(result);
                Logger.Information("Test {id}: {result}: {explanation}", result.TestId, result.Result.ToString(), result.Explanation);
                Raise(new TestProgress { Stage = TestProgressStage.Completed, Index = i, Total = tests.Count, Test = test, Result = result });
            }

            report.Cancelled = token.IsCancellationRequested;
            report.EndedUtc = DateTime.UtcNow;
            report.DurationMs = clock.ElapsedMilliseconds;
            foreach (TestResultClass resultClass in Enum.GetValues(typeof(TestResultClass)))
            {
                report.Summary[resultClass.ToString()] = report.Results.Count(r => r.Result == resultClass);
            }

            if (session.SeedWasUsed) report.Seed = session.Seed;
            if (session.Objects.Count > 0)
            {
                report.Chain = session.BuildChain();
                RedactChain(report.Chain, trimmed.Endpoint.TlsClientCertThumbprint);
            }
            session.Cleanup();
            return report;
        }

        private static void RedactChain(List<ChainRow> chain, string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint)) return;
            foreach (var row in chain)
            {
                foreach (var step in new[] { row.Accepted, row.Indexed, row.Retrievable, row.Intact, row.Committed }.Where(s => s != null))
                {
                    step.Explanation = step.Explanation?.Replace(thumbprint, CertificatePlaceholder);
                }
            }
        }

        private static async Task<TestResult> ExecuteOneAsync(ITestCase test, TestContext context, CancellationToken token)
        {
            try
            {
                // On the thread pool, so a test with a blocking first part cannot hold up the UI thread.
                var result = await Task.Run(() => test.ExecuteAsync(context, token), CancellationToken.None).ConfigureAwait(false);
                return result ?? Make(TestResultClass.TOOL_ERROR, "DCST4 internal error: the test returned no result.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Make(TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Test {id} threw an exception", test.Id);
                return Make(TestResultClass.TOOL_ERROR, $"DCST4 internal error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static TestResult Make(TestResultClass resultClass, string explanation) =>
            new TestResult { Result = resultClass, Explanation = explanation };

        /// <summary>The thumbprint stays out of result files, including where it is quoted in an error message.</summary>
        private static void Redact(TestResult result, string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint)) return;

            result.Explanation = result.Explanation?.Replace(thumbprint, CertificatePlaceholder);
            foreach (var key in result.Details.Keys.ToList())
            {
                result.Details[key] = result.Details[key]?.Replace(thumbprint, CertificatePlaceholder);
            }
            foreach (var entry in result.Timeline)
            {
                entry.Detail = entry.Detail?.Replace(thumbprint, CertificatePlaceholder);
            }
            if (result.CapabilityMatrix == null) return;
            foreach (var association in result.CapabilityMatrix.Associations)
            {
                association.Detail = association.Detail?.Replace(thumbprint, CertificatePlaceholder);
            }
        }

        private void Raise(TestProgress progress)
        {
            try
            {
                Progress?.Invoke(progress);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "A test progress handler failed");
            }
        }
    }
}
