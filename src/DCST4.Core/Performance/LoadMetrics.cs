using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DCST4.Core.Performance
{
    /// <summary>Percentiles of one kind of duration, in milliseconds.</summary>
    public sealed class LatencySummary
    {
        public long Count { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public double MaxMs { get; set; }
        public double MeanMs { get; set; }

        public static LatencySummary From(LatencyHistogram histogram) => new LatencySummary
        {
            Count = histogram.Count,
            P50Ms = Ms(histogram.Percentile(50)),
            P95Ms = Ms(histogram.Percentile(95)),
            P99Ms = Ms(histogram.Percentile(99)),
            MaxMs = Ms(histogram.MaxMicros),
            MeanMs = Math.Round(histogram.MeanMicros / 1000.0, 3),
        };

        internal static double Ms(long micros) => Math.Round(micros / 1000.0, 3);
    }

    /// <summary>How the attempts ended, by class. A C-STORE response is classed by its raw status, PS3.7 Annex C.</summary>
    public sealed class StatusClassCounts
    {
        public long Success { get; set; }
        public long Warning { get; set; }
        public long Failure { get; set; }
        public long Cancel { get; set; }
        /// <summary>No response within the response timeout.</summary>
        public long NoResponse { get; set; }
        /// <summary>The association was aborted or the connection closed while a request was outstanding.</summary>
        public long AssociationLost { get; set; }
        public long AssociationRejected { get; set; }
        /// <summary>The connection could not be opened, or the A-ASSOCIATE-RQ got no answer.</summary>
        public long ConnectFailed { get; set; }
        /// <summary>Every raw C-STORE status that came back, as 0xNNNN, with how many times.</summary>
        public SortedDictionary<string, long> ByCode { get; set; } = new SortedDictionary<string, long>(StringComparer.Ordinal);

        /// <summary>C-STOREs that were not stored: a failure or cancel status, no response, or a lost association.</summary>
        [JsonIgnore]
        public long NotStored => Failure + Cancel + NoResponse + AssociationLost;

        [JsonIgnore]
        public long Problems => NotStored + AssociationRejected + ConnectFailed;

        /// <summary>
        /// The problems in a few words, such as "3 not stored: 3 with status 0xA700", or null if there were none.
        /// The status codes are the raw ones, failure and cancel only.
        /// </summary>
        public string DescribeProblems()
        {
            if (Problems == 0) return null;
            var parts = new List<string>();
            foreach (var pair in ByCode)
            {
                if (!ushort.TryParse(pair.Key.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code)) continue;
                var state = Network.StoreStatus.Classify(code);
                if (state != Network.StoreState.Success && state != Network.StoreState.Warning) parts.Add($"{Count(pair.Value)} with status {pair.Key}");
            }
            if (NoResponse > 0) parts.Add($"{Count(NoResponse)} without a response");
            if (AssociationLost > 0) parts.Add($"{Count(AssociationLost)} on a lost association");

            var text = NotStored > 0 ? $"{Count(NotStored)} not stored: {string.Join(", ", parts)}" : string.Empty;
            var refused = new List<string>();
            if (AssociationRejected > 0) refused.Add($"{Count(AssociationRejected)} associations rejected");
            if (ConnectFailed > 0) refused.Add($"{Count(ConnectFailed)} connections failed");
            if (refused.Count > 0) text += (text.Length > 0 ? "; " : string.Empty) + string.Join(", ", refused);
            return text;
        }

        private static string Count(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        public void Add(StatusClassCounts other)
        {
            Success += other.Success;
            Warning += other.Warning;
            Failure += other.Failure;
            Cancel += other.Cancel;
            NoResponse += other.NoResponse;
            AssociationLost += other.AssociationLost;
            AssociationRejected += other.AssociationRejected;
            ConnectFailed += other.ConnectFailed;
            foreach (var pair in other.ByCode)
            {
                ByCode.TryGetValue(pair.Key, out var count);
                ByCode[pair.Key] = count + pair.Value;
            }
        }

        public StatusClassCounts Clone()
        {
            var copy = new StatusClassCounts();
            copy.Add(this);
            return copy;
        }
    }

    /// <summary>One second of a load run, by when the responses arrived. The rows of the series file and the live chart.</summary>
    public sealed class LoadSecond
    {
        /// <summary>From the start of the run, counted from 0.</summary>
        public int Second { get; set; }
        /// <summary>The ramp step, counted from 1. 0 outside a ramp.</summary>
        public int Step { get; set; }
        /// <summary>Within the warm-up, so not in the summary.</summary>
        public bool WarmUp { get; set; }
        /// <summary>The last second of a run or ramp step, which ended before the second was over, so its counts are not per whole second.</summary>
        public bool Partial { get; set; }
        public int OpenAssociations { get; set; }
        /// <summary>C-STOREs that ended in this second, however they ended.</summary>
        public long Attempts { get; set; }
        /// <summary>Success or Warning.</summary>
        public long Stored { get; set; }
        /// <summary>Not stored, see <see cref="StatusClassCounts.NotStored"/>.</summary>
        public long Failed { get; set; }
        public long Bytes { get; set; }
        public double MegabytesPerSecond { get; set; }
        public double LatencyP50Ms { get; set; }
        public double LatencyP95Ms { get; set; }
        public double LatencyMaxMs { get; set; }
        /// <summary>The share of all logical processors that this process used, so that a busy client can be seen.</summary>
        public double ClientCpuPercent { get; set; }
    }

    /// <summary>What one measured window gave: a run, a ramp step or a soak window.</summary>
    public sealed class LoadSummary
    {
        /// <summary>The measured window, after the warm-up, on the monotonic clock.</summary>
        public long WindowMs { get; set; }
        public long Attempts { get; set; }
        public long Stored { get; set; }
        public long Bytes { get; set; }
        public double StoredPerSecond { get; set; }
        public double MegabytesPerSecond { get; set; }
        /// <summary>First byte of the C-STORE-RQ written to the whole C-STORE-RSP received.</summary>
        public LatencySummary Latency { get; set; }
        /// <summary>Last byte of the dataset handed to the socket to the whole C-STORE-RSP received.</summary>
        public LatencySummary ResponseWait { get; set; }
        /// <summary>Start of the TCP connection to the A-ASSOCIATE-AC. All associations, also those in the warm-up.</summary>
        public LatencySummary AssociationSetup { get; set; }
        /// <summary>The TCP connection, and the TLS handshake if there is one, which is part of <see cref="AssociationSetup"/>.</summary>
        public LatencySummary Connect { get; set; }
        public int AssociationsOpened { get; set; }
        public StatusClassCounts Errors { get; set; }
        /// <summary>Attempts not stored, as a share of attempts. 0 to 1.</summary>
        public double ErrorRate { get; set; }
    }

    public sealed class RampStep
    {
        /// <summary>Counted from 1.</summary>
        public int Step { get; set; }
        public int Associations { get; set; }
        public LoadSummary Summary { get; set; }
        /// <summary>Where saturation was found.</summary>
        public bool Saturated { get; set; }
    }

    public sealed class SaturationVerdict
    {
        public bool Found { get; set; }
        /// <summary>Counted from 1. 0 when none was found.</summary>
        public int Step { get; set; }
        public int Associations { get; set; }
        /// <summary>throughput, latency or errors.</summary>
        public string Reason { get; set; }
        /// <summary>The step with the highest throughput, counted from 1.</summary>
        public int BestStep { get; set; }
        public int BestAssociations { get; set; }
        public string Description { get; set; }
    }

    public sealed class SoakWindow
    {
        /// <summary>Counted from 1.</summary>
        public int Window { get; set; }
        public int StartSecond { get; set; }
        public int EndSecond { get; set; }
        public LoadSummary Summary { get; set; }
    }

    public sealed class DegradationVerdict
    {
        public bool Degraded { get; set; }
        /// <summary>p95-trend, throughput-drop or errors.</summary>
        public List<string> Reasons { get; set; } = new List<string>();
        public double BaselineP95Ms { get; set; }
        public double EndP95Ms { get; set; }
        /// <summary>What the least-squares trend of p95 rises by over the run, in percent of the baseline.</summary>
        public double P95TrendPercent { get; set; }
        public double BaselineStoredPerSecond { get; set; }
        public double EndStoredPerSecond { get; set; }
        /// <summary>Too few windows to say anything.</summary>
        public bool TooShort { get; set; }
        public string Description { get; set; }
    }

    /// <summary>How long stored instances took to be found by C-FIND. Separate from the load metrics.</summary>
    public sealed class TimeToAvailable
    {
        public int SampleEvery { get; set; }
        public double IntervalSeconds { get; set; }
        public double TimeoutSeconds { get; set; }
        public int Sampled { get; set; }
        public int Found { get; set; }
        public int NeverFound { get; set; }
        /// <summary>Still being polled when the run was cancelled.</summary>
        public int NotSettled { get; set; }
        /// <summary>False when the C-FIND SOP class was not accepted. Null if no query went out.</summary>
        public bool? QuerySupported { get; set; }
        public int FailedRounds { get; set; }
        public int Rounds { get; set; }
        public int Queries { get; set; }
        /// <summary>From the C-STORE-RSP to the C-FIND response that found it. The resolution is the poll interval.</summary>
        public LatencySummary Available { get; set; }
        public string Note { get; set; }
    }

    /// <summary>The state of the computer that ran the load, to tell a busy client from a slow system under test.</summary>
    public sealed class ClientHealth
    {
        public int LogicalProcessors { get; set; }
        public bool ServerGc { get; set; }
        public string GcLatencyMode { get; set; }
        public int MinWorkerThreads { get; set; }
        public int MinCompletionThreads { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public double MeanCpuPercent { get; set; }
        public double MaxCpuPercent { get; set; }
    }
}
