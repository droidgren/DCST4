using DCST4.Core.Generation.Datasets;
using DCST4.Core.Testing.Cases;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Testing
{
    /// <summary>The tests DCST4 can run. Both front-ends select from this list.</summary>
    public static class TestCatalog
    {
        public const string SelectAll = "all";

        public static IReadOnlyList<ITestCase> All { get; } = Create();

        /// <summary>In the order they depend on each other: a query or a retrieve looks for what the store tests before it stored.</summary>
        private static IReadOnlyList<ITestCase> Create()
        {
            var tests = new List<ITestCase>
            {
                new CEchoTest(),
                new NegotiationProbeTest(),
                new UnknownCallingAeTest(),
                new WrongCalledAeTest(),
                new UnsupportedContextsTest(),
            };

            tests.AddRange(DatasetProfileCatalog.Default.Select(p => (ITestCase)new StoreObjectTest(p)));
            tests.Add(new StoreObjectTest(DatasetProfileCatalog.Find("ct"), "ct-edge", EdgeCases.PrivateTags | EdgeCases.LongStrings | EdgeCases.NestedSequences));

            tests.Add(new QueryIndexTest());
            tests.Add(new QueryLevelsTest());
            tests.Add(new QueryReturnKeysTest());
            tests.Add(new QueryMatchingTest());
            tests.Add(new RetrieveGetTest());
            tests.Add(new RetrieveMoveTest());
            tests.Add(new RetrieveMoveDiagnosticTest());
            tests.Add(new VerifyIntegrityTest());
            tests.Add(new CommitStoredTest());
            tests.Add(new CommitUnknownInstanceTest());
            tests.Add(new DuplicateIdenticalTest());
            tests.Add(new DuplicateModifiedTest());
            tests.Add(new ConflictDemographicsTest());

            // Limits and faults. They depend on nothing above, and are last because they strain the system under test.
            tests.Add(new TlsAssociationTest());
            tests.Add(new TlsServerCertificateTest());
            tests.Add(new TlsWrongAuthorityTest());
            tests.Add(new TlsExpiredCertificateTest());
            tests.Add(new MaxPduTest());
            tests.Add(new OversizedPduTest());
            tests.Add(new AbortMidDatasetTest());
            tests.Add(new DropMidDatasetTest());
            tests.Add(new DropBeforeResponseTest());
            tests.Add(new MaxAssociationsTest());
            tests.Add(new IdleTimeoutTest());
            tests.Add(new ArtimTest());
            return tests;
        }

        /// <summary>
        /// Picks tests by a comma-separated list of ids, categories or "all", ignoring case, in catalog order.
        /// Returns null and sets <paramref name="error"/> if the list is empty or names something unknown.
        /// </summary>
        public static IReadOnlyList<ITestCase> Select(string selection, out string error, IReadOnlyList<ITestCase> catalog = null)
        {
            catalog = catalog ?? All;
            error = null;

            var items = (selection ?? string.Empty).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (items.Count == 0)
            {
                error = "No tests were selected. " + Available(catalog);
                return null;
            }

            var selected = new HashSet<ITestCase>();
            foreach (var item in items)
            {
                var matches = Matching(item, catalog);
                if (matches.Count == 0)
                {
                    error = $"Unknown test or category '{item}'. " + Available(catalog);
                    return null;
                }
                selected.UnionWith(matches);
            }
            return catalog.Where(selected.Contains).ToList();
        }

        private static List<ITestCase> Matching(string item, IReadOnlyList<ITestCase> catalog)
        {
            if (string.Equals(item, SelectAll, StringComparison.OrdinalIgnoreCase)) return catalog.ToList();
            return catalog.Where(t =>
                string.Equals(t.Id, item, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Category.ToString(), item, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private static string Available(IReadOnlyList<ITestCase> catalog) =>
            $"Tests: {string.Join(", ", catalog.Select(t => t.Id))}. Categories: {string.Join(", ", Enum.GetNames(typeof(TestCategory)))}. Or: {SelectAll}.";
    }
}
