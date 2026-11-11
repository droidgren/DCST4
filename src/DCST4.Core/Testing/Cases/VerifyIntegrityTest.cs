using DCST4.Core.Network;
using DCST4.Core.Verification;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>Compares everything the retrieve tests brought back with what was sent: the "intact" step of the chain.</summary>
    public sealed class VerifyIntegrityTest : ITestCase
    {
        public string Id => "verify-integrity";
        public string Name => "Integrity: retrieved objects against what was sent";
        public TestCategory Category => TestCategory.Integrity;
        public string Description => "Compares every object the C-GET and C-MOVE tests brought back with what was sent. Needs the store and retrieve tests in the same run.";
        public string Group => TestGroups.Retrieve;
        public bool StoresObjects => false;

        public Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var records = context.Session.Objects.Where(o => o.Retrieves.Any(ToCompare)).ToList();
            if (records.Count == 0)
            {
                return Task.FromResult(new TestResult
                {
                    Result = TestResultClass.INDETERMINATE,
                    Explanation = "This test compares what the retrieve tests brought back, and nothing was retrieved in this run.",
                });
            }

            var report = new IntegrityReport();
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();

                var expected = record.Stored.Select(i => new ExpectedInstance { Manifest = i.Manifest, SavedPath = i.SavedPath }).ToList();
                var sets = new List<IntegritySet>();
                foreach (var retrieve in record.Retrieves.Where(ToCompare))
                {
                    var returned = retrieve.Outcome.Files.Select(f => new ReturnedInstance { Path = f.Path, OfferedTransferSyntaxUids = f.ProposedTransferSyntaxUids }).ToList();
                    sets.Add(IntegrityOracle.Compare(record.ObjectType, RetrieveNames.Of(retrieve.Method), expected, returned));
                }

                report.Sets.AddRange(sets);
                record.Intact = Summarize(sets);
            }

            var result = new TestResult { Integrity = report };
            var worst = ResultSeverity.Worst(report.Sets.Select(s => s.Result));
            var whole = report.Sets.Count(s => s.Result == TestResultClass.PASS);
            result.Details["sets"] = Fixtures.Int(report.Sets.Count);
            result.Details["intact"] = Fixtures.Int(whole);

            var firstBad = report.Sets.FirstOrDefault(s => s.Result == worst && worst != TestResultClass.PASS);
            var explanation = firstBad != null
                ? $"{Fixtures.Int(whole)} of {Fixtures.Int(report.Sets.Count)} retrieves were intact. {firstBad.ObjectType} by {firstBad.Method}: {Describe(firstBad, worst)}"
                : $"{Fixtures.Int(report.Sets.Count)} of {Fixtures.Int(report.Sets.Count)} retrieves were intact.";
            Fixtures.Set(result, worst, explanation);
            return Task.FromResult(result);
        }

        /// <summary>Something came back, or the system said it was done and nothing did, which is a Transport finding. A retrieve that never got that far says nothing.</summary>
        private static bool ToCompare(RetrieveRecord retrieve) => retrieve.Outcome.Files.Count > 0 || retrieve.Step.Result == TestResultClass.FAIL;

        private static ChainStep Summarize(IReadOnlyCollection<IntegritySet> sets)
        {
            var worst = ResultSeverity.Worst(sets.Select(s => s.Result));
            var firstBad = sets.FirstOrDefault(s => s.Result == worst && worst != TestResultClass.PASS);
            return ChainStep.Of(worst, firstBad != null ? $"{firstBad.Method}: {Describe(firstBad, worst)}" : "Nothing differs from what was sent.");
        }

        private static string Describe(IntegritySet set, TestResultClass worst)
        {
            var level = set.Levels.First(l => l.Result == worst);
            return $"{level.Level}: {level.Explanation}";
        }
    }
}
