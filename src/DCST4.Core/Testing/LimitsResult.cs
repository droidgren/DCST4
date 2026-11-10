using DCST4.Core.Network;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DCST4.Core.Testing
{
    /// <summary>What was left of an instance on the system under test.</summary>
    public enum InstanceState
    {
        /// <summary>The question could not be answered, for example because C-FIND is not supported.</summary>
        Unknown,
        /// <summary>A query does not find it.</summary>
        None,
        /// <summary>It is found, but what comes back is not what was sent.</summary>
        Partial,
        /// <summary>It is found, and what comes back has the pixels that were sent.</summary>
        Complete,
        /// <summary>It is found, but could not be retrieved to compare.</summary>
        PresentUnverified,
    }

    /// <summary>How the system under test took the same object again after a broken transfer.</summary>
    public enum RetryOutcome
    {
        NotTried,
        Stored,
        StoredWithWarning,
        /// <summary>Refused with 0x0111, Duplicate SOP Instance.</summary>
        RefusedAsDuplicate,
        Refused,
        /// <summary>No C-STORE response: the association failed, was rejected or timed out.</summary>
        NoResponse,
    }

    /// <summary>One association of the max PDU test.</summary>
    public sealed class PduAttempt
    {
        /// <summary>The largest PDU DCST4 said it takes.</summary>
        public long OurMaxPdu { get; set; }
        /// <summary>The largest PDU the system under test said it takes. 0 means no limit.</summary>
        public long? PeerMaxPdu { get; set; }
        /// <summary>The length of the data PDUs that DCST4 sent, all but the last.</summary>
        public long? PduLengthSent { get; set; }
        public long? LargestPduSent { get; set; }
        public long? LargestPduReceived { get; set; }
        /// <summary>Raw (0000,0900) of the C-STORE response as 0xNNNN, or null if none came.</summary>
        public string Status { get; set; }
        /// <summary>stored, refused, not-accepted, association-rejected, ended or timeout.</summary>
        public string Outcome { get; set; }
        public string SopInstanceUid { get; set; }
    }

    /// <summary>A transfer that DCST4 broke off on purpose, and what followed.</summary>
    public sealed class FaultRecord
    {
        /// <summary>abort-mid-dataset, drop-mid-dataset or drop-before-response.</summary>
        public string Kind { get; set; }
        public long DatasetBytes { get; set; }
        public long DatasetBytesSent { get; set; }
        public long PduLength { get; set; }
        /// <summary>Whether a C-STORE response reached this computer although the connection was closed without reading it. Only for drop-before-response.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ResponseArrivedUnread { get; set; }
        public InstanceState StateAfterFault { get; set; }
        public RetryOutcome Retry { get; set; }
        /// <summary>Raw status of the retry as 0xNNNN, or null if none came.</summary>
        public string RetryStatus { get; set; }
        public InstanceState StateAfterRetry { get; set; }
    }

    /// <summary>What the limit, fault and TLS tests found. Each test fills the part that is its own.</summary>
    public sealed class LimitsResult
    {
        // ---- limit-max-associations
        /// <summary>How many associations DCST4 was told to stop at.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Ceiling { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? AssociationsOpened { get; set; }
        /// <summary>The number of simultaneous associations the system under test allows. Null if the ceiling was reached first.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? AssociationLimit { get; set; }
        /// <summary>How the association over the limit was refused: A-ASSOCIATE-RJ, A-ABORT, connection-closed, connection-refused or no-answer.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string RefusedWith { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AssociationRejection Rejection { get; set; }
        /// <summary>How many of the opened associations still answered a C-ECHO at the end.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? AliveAtEnd { get; set; }

        // ---- limit-idle-timeout and limit-artim
        /// <summary>How long DCST4 was told to wait.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? WaitSeconds { get; set; }
        /// <summary>From the A-ASSOCIATE-AC, or from the TCP connection for ARTIM, until the other side ended it. On the monotonic clock. Null if it stayed open.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? EndedAfterMs { get; set; }
        /// <summary>A-ABORT, A-RELEASE-RQ, connection-closed, connection-reset, or still-open.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string EndedWith { get; set; }

        // ---- limit-max-pdu and limit-oversized-pdu
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PduAttempt> PduAttempts { get; set; }

        // ---- fault-*
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public FaultRecord Fault { get; set; }

        // ---- tls-*
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TlsSessionInfo Tls { get; set; }
        /// <summary>Which client certificate the test presented, in words. Never a thumbprint.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ClientCertificate { get; set; }
    }
}
