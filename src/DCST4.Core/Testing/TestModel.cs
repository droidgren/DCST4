using DCST4.Core.Capability;
using DCST4.Core.Network;
using DCST4.Core.TestData;
using DCST4.Core.Verification;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing
{
    public enum TestCategory { Capability, Integrity, Limits, Performance }

    /// <summary>The names are written to the result files as they stand. Renaming one breaks comparison across versions.</summary>
    public enum TestResultClass
    {
        PASS,
        FAIL,
        NOT_SUPPORTED,
        EXPECTED_FAILURE,
        TIMEOUT,
        INDETERMINATE,
        /// <summary>Our own bug or local problem. Never blames the system under test.</summary>
        TOOL_ERROR,
    }

    public sealed class TestContext
    {
        public TestContext(TargetProfile profile, TestPatientNaming patientNaming = TestPatientNaming.RegistrySwedishName, TestRunSession session = null)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            PatientNaming = patientNaming;
            Session = session ?? new TestRunSession();
        }

        public TargetProfile Profile { get; }

        /// <summary>How the datasets of this run name the patient. Given by the front-end, since Core does not read its settings.</summary>
        public TestPatientNaming PatientNaming { get; }

        /// <summary>What the tests of this run hand on to each other.</summary>
        public TestRunSession Session { get; }
    }

    public interface ITestCase
    {
        /// <summary>Stable across versions: result files are compared by it.</summary>
        string Id { get; }
        string Name { get; }
        TestCategory Category { get; }
        Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token);
    }

    /// <summary>A raw DIMSE status as the system under test sent it.</summary>
    public sealed class DimseStatusRecord
    {
        public string Operation { get; set; }
        /// <summary>0xNNNN</summary>
        public string Code { get; set; }
        public string Description { get; set; }
    }

    /// <summary>The outcome of one test. <see cref="TestRunner"/> fills in identity and times.</summary>
    public sealed class TestResult
    {
        public string TestId { get; set; }
        public string Name { get; set; }
        public TestCategory Category { get; set; }
        public TestResultClass Result { get; set; }
        /// <summary>Must not hold measured times or anything else that differs between identical runs.</summary>
        public string Explanation { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime EndedUtc { get; set; }
        /// <summary>From the monotonic clock.</summary>
        public long DurationMs { get; set; }
        public List<DimseStatusRecord> StatusCodes { get; set; } = new List<DimseStatusRecord>();
        public List<NegotiatedContext> NegotiatedContexts { get; set; } = new List<NegotiatedContext>();
        public SortedDictionary<string, string> Uids { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public SortedDictionary<string, string> Details { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public List<ProtocolEvent> Timeline { get; set; } = new List<ProtocolEvent>();
        /// <summary>Only set by the negotiation probe.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CapabilityMatrix CapabilityMatrix { get; set; }
        /// <summary>Only set by verify-integrity.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IntegrityReport Integrity { get; set; }
        /// <summary>Only set by the query capability tests.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public QueryReport Query { get; set; }
        /// <summary>Only set by the Storage Commitment tests.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CommitmentResult Commitment { get; set; }
        /// <summary>Only set by the limit, fault and TLS tests.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LimitsResult Limits { get; set; }
    }

    /// <summary>
    /// The profile as written to result files. Fields are listed one by one so that a new profile setting, such as the client
    /// certificate thumbprint, cannot end up in a result file by accident.
    /// </summary>
    public sealed class ResultProfile
    {
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string CallingAe { get; set; }
        public string CalledAe { get; set; }
        public bool UseTls { get; set; }
        public bool TlsIgnoreCertErrors { get; set; }
        public string LocalScpAe { get; set; }
        public int LocalScpPort { get; set; }
        public string UidRoot { get; set; }
        public int ConnectionTimeoutSeconds { get; set; }
        public int AssociationTimeoutSeconds { get; set; }
        public int ResponseTimeoutSeconds { get; set; }

        public static ResultProfile From(TargetProfile profile) => new ResultProfile
        {
            Name = profile.Name,
            Host = profile.Endpoint.Host,
            Port = profile.Endpoint.Port,
            CallingAe = profile.Endpoint.CallingAe,
            CalledAe = profile.Endpoint.CalledAe,
            UseTls = profile.Endpoint.UseTls,
            TlsIgnoreCertErrors = profile.Endpoint.TlsIgnoreCertErrors,
            LocalScpAe = profile.LocalScpAe,
            LocalScpPort = profile.LocalScpPort,
            UidRoot = profile.UidRoot,
            ConnectionTimeoutSeconds = profile.Network.ConnectionTimeoutSeconds,
            AssociationTimeoutSeconds = profile.Network.AssociationTimeoutSeconds,
            ResponseTimeoutSeconds = profile.Network.ResponseTimeoutSeconds,
        };
    }

    /// <summary>A whole test run: what is written to the result files.</summary>
    public sealed class TestRunReport
    {
        /// <summary>
        /// 2: added the capability matrix and the association number on protocol events. 3: added the seed, the chain summary,
        /// the integrity report and the query report. 4: added the commitment report and the committed step of the chain.
        /// 5: added the limits section of the limit, fault and TLS tests.
        /// </summary>
        public const int CurrentSchemaVersion = 5;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string ToolVersion { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime EndedUtc { get; set; }
        /// <summary>From the monotonic clock.</summary>
        public long DurationMs { get; set; }
        public bool Cancelled { get; set; }
        public ResultProfile Profile { get; set; }
        /// <summary>Number of results per class, including those with none.</summary>
        public SortedDictionary<string, int> Summary { get; set; } = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public List<TestResult> Results { get; set; } = new List<TestResult>();
        /// <summary>What the generated data of this run followed from. Null if the run generated none.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Seed { get; set; }
        /// <summary>One row per object type that a store test handled. Null if there were none.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ChainRow> Chain { get; set; }
    }

    /// <summary>What the command line returns.</summary>
    public static class RunExitCodes
    {
        public const int Clean = 0;
        public const int Failures = 1;
        public const int Usage = 2;
        public const int Cancelled = 130;

        public static int From(TestRunReport report)
        {
            if (report.Cancelled) return Cancelled;
            return report.Results.Exists(r => r.Result == TestResultClass.FAIL || r.Result == TestResultClass.TOOL_ERROR) ? Failures : Clean;
        }
    }

    public enum TestProgressStage { Started, Completed }

    public sealed class TestProgress
    {
        public TestProgressStage Stage { get; set; }
        /// <summary>Position in the run, from 0.</summary>
        public int Index { get; set; }
        public int Total { get; set; }
        public ITestCase Test { get; set; }
        /// <summary>Null while <see cref="Stage"/> is Started.</summary>
        public TestResult Result { get; set; }
    }
}
