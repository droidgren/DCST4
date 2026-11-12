using DCST4.Core.Performance;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace DCST4.Core.Testing
{
    /// <summary>What a load, ramp or soak test measured. The measured numbers are here and never in the explanation.</summary>
    public sealed class PerformanceResult
    {
        /// <summary>Load, Ramp or Soak.</summary>
        public LoadMode Mode { get; set; }
        public LoadSettings Settings { get; set; }
        public string Mix { get; set; }
        public string MixDescription { get; set; }
        public string TransferSyntaxUid { get; set; }
        public int PoolInstances { get; set; }
        /// <summary>The mean size of a send on the wire, header and pixel data.</summary>
        public long AverageObjectBytes { get; set; }
        /// <summary>What DCST4 announced and sent with, at most what the system announced.</summary>
        public uint MaxPdu { get; set; }
        public int PlannedInstances { get; set; }
        public double PlannedMegabytes { get; set; }
        /// <summary>duration, count, cancelled, not-supported, timeout, connect-failed or workers-stopped.</summary>
        public string StoppedBy { get; set; }
        /// <summary>The instance ceiling ended a run that was meant to last for a duration, so the summary covers a shorter window.</summary>
        public bool EndedByCeiling { get; set; }
        /// <summary>For the load test the whole run, for the ramp the step with the highest throughput, for the soak the whole soak.</summary>
        public LoadSummary Summary { get; set; }
        /// <summary>How many seconds the series has.</summary>
        public int Seconds { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RampStep> Steps { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SaturationVerdict Saturation { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SoakWindow> Windows { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DegradationVerdict Degradation { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TimeToAvailable TimeToAvailable { get; set; }
        public ClientHealth Client { get; set; }
        /// <summary>The series file, one row per second, written while the test ran. Null if no folder was given.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string SeriesFile { get; set; }
        /// <summary>The soak's windows file. Null for the other tests.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string WindowsFile { get; set; }

        /// <summary>
        /// How the run stopped, in the words the explanation, the report and the Performance tab share. Says that Max
        /// instances ran out before the duration as information, never as a fault.
        /// </summary>
        public string DescribeStop()
        {
            var planned = PlannedInstances.ToString("N0", CultureInfo.InvariantCulture);
            if (EndedByCeiling)
            {
                var duration = (Settings?.DurationSeconds ?? 0).ToString("0.##", CultureInfo.InvariantCulture);
                var what = (Summary?.Errors?.NotStored ?? 0) > 0 ? "stored or refused" : "stored";
                return $"All {planned} instances (Max instances) were {what} after {Seconds.ToString(CultureInfo.InvariantCulture)} s of the {duration} s, so throughput and latency come from a shorter window than asked for.";
            }
            switch (StoppedBy)
            {
                case "duration": return "Ran for the whole duration.";
                case "count": return $"Stopped after all {planned} instances.";
                default: return "Stopped by " + StoppedBy + ".";
            }
        }
    }
}
