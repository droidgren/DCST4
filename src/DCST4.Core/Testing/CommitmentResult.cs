using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DCST4.Core.Testing
{
    /// <summary>The names are written to the result files as they stand.</summary>
    public enum CommitmentInstanceOutcome
    {
        /// <summary>In the Referenced SOP Sequence of the report.</summary>
        Committed,
        /// <summary>In the Failed SOP Sequence.</summary>
        Failed,
        /// <summary>In neither, which the standard does not allow.</summary>
        NotReported,
        /// <summary>More than once, or in both lists.</summary>
        ListedTwice,
    }

    /// <summary>What the report said about one instance that was asked for.</summary>
    public sealed class CommitmentInstanceResult
    {
        /// <summary>The store test's object type, or empty for the instance that was never stored.</summary>
        public string ObjectType { get; set; }
        public string SopClassUid { get; set; }
        public string SopInstanceUid { get; set; }
        /// <summary>The UID that commit-unknown-instance adds, which no test stored.</summary>
        public bool NeverStored { get; set; }
        public CommitmentInstanceOutcome Outcome { get; set; }
        /// <summary>0xNNNN, or null if the instance did not fail or the item had no Failure Reason.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string FailureReason { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string FailureReasonText { get; set; }
    }

    /// <summary>An N-EVENT-REPORT that our side refused.</summary>
    public sealed class RejectedCommitmentReport
    {
        public string Via { get; set; }
        public string CallingAe { get; set; }
        public string TransactionUid { get; set; }
        public int EventTypeId { get; set; }
        /// <summary>What we answered, as 0xNNNN.</summary>
        public string AnsweredStatus { get; set; }
        public string Problem { get; set; }
    }

    /// <summary>One Storage Commitment request and what came of it. The times are on the monotonic clock.</summary>
    public sealed class CommitmentResult
    {
        public string TransactionUid { get; set; }
        /// <summary>How long the association stayed open after the N-ACTION-RSP. 0 means released at once.</summary>
        public int LingerSeconds { get; set; }
        public int WaitSeconds { get; set; }
        public int GraceSeconds { get; set; }
        /// <summary>The raw N-ACTION-RSP status as 0xNNNN, or null if none came.</summary>
        public string ActionStatus { get; set; }
        /// <summary>SameAssociation or SeparateAssociation, or null if no report came in time.</summary>
        public string ReportVia { get; set; }
        /// <summary>Who sent the report.</summary>
        public string ReportCallingAe { get; set; }
        public int? EventTypeId { get; set; }
        /// <summary>From the N-ACTION going out to the report arriving, in time or late.</summary>
        public long? ReportAfterMs { get; set; }
        /// <summary>The report came after the wait, while DCST4 was still listening.</summary>
        public bool Late { get; set; }
        /// <summary>Which association the late report came on.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string LateVia { get; set; }
        public List<CommitmentInstanceResult> Instances { get; set; } = new List<CommitmentInstanceResult>();
        /// <summary>SOP Instance UIDs in the report that were never asked for.</summary>
        public List<string> UnrequestedInstances { get; set; } = new List<string>();
        public List<RejectedCommitmentReport> RejectedReports { get; set; } = new List<RejectedCommitmentReport>();
        /// <summary>Reports of the same transaction after the first.</summary>
        public int RepeatedReports { get; set; }
    }
}
