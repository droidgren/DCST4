using System.Collections.Generic;

namespace DCST4.Core.Testing
{
    /// <summary>One query/level combination.</summary>
    public sealed class QueryLevelRow
    {
        public string Root { get; set; }
        public string Level { get; set; }
        public TestResultClass Result { get; set; }
        /// <summary>0xNNNN of the final response, or null if none came.</summary>
        public string Status { get; set; }
        public int Matches { get; set; }
        public string Explanation { get; set; }
    }

    public enum ReturnKeyState { Populated, Empty, Absent }

    /// <summary>One return key at one level.</summary>
    public sealed class QueryKeyRow
    {
        public string Level { get; set; }
        public string Keyword { get; set; }
        public string Tag { get; set; }
        /// <summary>A key the standard requires the SCP to support at this level, as opposed to an optional one.</summary>
        public bool Required { get; set; }
        public ReturnKeyState State { get; set; }
        public string Returned { get; set; }
        /// <summary>Null when the object has no such attribute to compare with, as for a count.</summary>
        public bool? MatchesOriginal { get; set; }
    }

    /// <summary>One matching behaviour tried.</summary>
    public sealed class QueryMatchRow
    {
        public string Case { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
        /// <summary>The character set the query itself was sent in.</summary>
        public string QueryCharacterSet { get; set; }
        /// <summary>The fewest matches that do not count as a failure.</summary>
        public int MinimumMatches { get; set; }
        public int ExpectedMatches { get; set; }
        public int Matches { get; set; }
        public TestResultClass Result { get; set; }
        public string Note { get; set; }
    }

    /// <summary>What the query capability tests found. Each test fills its own part.</summary>
    public sealed class QueryReport
    {
        public List<QueryLevelRow> Levels { get; set; } = new List<QueryLevelRow>();
        public List<QueryKeyRow> ReturnKeys { get; set; } = new List<QueryKeyRow>();
        public List<QueryMatchRow> Matching { get; set; } = new List<QueryMatchRow>();
    }
}
