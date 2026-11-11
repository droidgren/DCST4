using System.Collections.Generic;

namespace DCST4.Core.Testing
{
    /// <summary>
    /// What a front-end groups the tests by, see <see cref="ITestCase.Group"/>. Finer than <see cref="TestCategory"/> and only
    /// for display: result files and the command line's selection go by id and category, never by group.
    /// </summary>
    public static class TestGroups
    {
        public const string Association = "Association";
        public const string Store = "Store";
        public const string Query = "Query";
        public const string Retrieve = "Retrieve & integrity";
        public const string Commitment = "Storage Commitment";
        public const string Duplicates = "Duplicates";
        public const string Tls = "TLS";
        public const string Limits = "Limits";
        public const string Faults = "Faults";
        public const string Performance = "Performance";

        /// <summary>In the order they are shown. Within a group the tests keep their catalog order, which is the order they run in.</summary>
        public static IReadOnlyList<string> All { get; } = new[] { Association, Store, Query, Retrieve, Commitment, Duplicates, Tls, Limits, Faults, Performance };
    }
}
