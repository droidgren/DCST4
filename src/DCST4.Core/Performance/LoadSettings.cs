using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Performance
{
    public enum LoadMode { Load, Ramp, Soak }

    /// <summary>What the load, ramp and soak tests do. Every run has a ceiling of instances, so what it can leave on the system is known before it starts.</summary>
    public sealed class LoadSettings
    {
        public const int LargestAssociations = 256;
        public const int DefaultAssociations = 4;
        public const double DefaultDurationSeconds = 20;
        public const double DefaultWarmUpSeconds = 2;
        public const int DefaultLoadMaxInstances = 1000;
        public const int DefaultRampMaxInstances = 2000;
        public const double DefaultRampStepSeconds = 10;
        public const double DefaultSoakSeconds = 30 * 60;
        public const double DefaultSoakRate = 2;
        public const double DefaultSnapshotSeconds = 60;
        public const int DefaultSampleEvery = 50;
        public const double DefaultPollIntervalSeconds = 2;
        public const double DefaultPollTimeoutSeconds = 60;
        public const string DefaultMix = "ct";
        /// <summary>A new study and series per this many instances of one worker.</summary>
        public const int InstancesPerStudy = 500;
        /// <summary>The time-to-available poller sends at most this many C-FINDs per round.</summary>
        public const int MaxQueriesPerRound = 10;

        /// <summary>What fo-dicom announces by default, so that a load run uses the same PDUs as an ordinary send.</summary>
        public static uint DefaultMaxPdu => new DicomServiceOptions().MaxPDULength;

        public static IReadOnlyList<int> DefaultRampSteps { get; } = new[] { 1, 2, 4, 8 };

        // ------------------------------------------------------------ load
        public int Associations { get; set; } = DefaultAssociations;
        /// <summary>Release and open a new association after this many. 0 keeps one association for the whole run.</summary>
        public int InstancesPerAssociation { get; set; }
        public double DurationSeconds { get; set; } = DefaultDurationSeconds;
        /// <summary>Send exactly this many instead of running for a duration. 0 runs for <see cref="DurationSeconds"/>.</summary>
        public int InstanceCount { get; set; }
        /// <summary>Instances per second over all associations. 0 sends as fast as the system answers.</summary>
        public double RatePerSecond { get; set; }
        public string Mix { get; set; } = DefaultMix;
        /// <summary>The maximum PDU length DCST4 announces and sends with, at most what the system announces. 0 is fo-dicom's default.</summary>
        public uint MaxPdu { get; set; }
        /// <summary>Seconds at the start of the run, and of each ramp step, that are in the series but not in the summary.</summary>
        public double WarmUpSeconds { get; set; } = DefaultWarmUpSeconds;
        /// <summary>The most instances the run sends. 0 is the mode's default.</summary>
        public int MaxInstances { get; set; }

        // ------------------------------------------------------------ ramp
        public List<int> RampSteps { get; set; } = DefaultRampSteps.ToList();
        public double RampStepSeconds { get; set; } = DefaultRampStepSeconds;
        /// <summary>Throughput must rise by at least this much over the best earlier step.</summary>
        public double RampMinGainPercent { get; set; } = SaturationDetector.DefaultMinGainPercent;
        /// <summary>p95 latency above this many times that of step 1 counts as saturated.</summary>
        public double RampLatencyFactor { get; set; } = SaturationDetector.DefaultLatencyFactor;
        /// <summary>An error rate this many percentage points above the step before counts as saturated.</summary>
        public double RampErrorRisePoints { get; set; } = SaturationDetector.DefaultErrorRisePoints;

        // ------------------------------------------------------------ soak
        public double SoakSeconds { get; set; } = DefaultSoakSeconds;
        public double SoakRatePerSecond { get; set; } = DefaultSoakRate;
        public double SnapshotSeconds { get; set; } = DefaultSnapshotSeconds;
        public double SoakLatencyRisePercent { get; set; } = DegradationDetector.DefaultLatencyRisePercent;
        public double SoakThroughputDropPercent { get; set; } = DegradationDetector.DefaultThroughputDropPercent;

        // ------------------------------------------------------------ time-to-available
        /// <summary>Query for 1 in this many stored instances. 0 turns the polling off. The ramp never polls.</summary>
        public int SampleEvery { get; set; } = DefaultSampleEvery;
        public double PollIntervalSeconds { get; set; } = DefaultPollIntervalSeconds;
        public double PollTimeoutSeconds { get; set; } = DefaultPollTimeoutSeconds;

        /// <summary>Replaces the mix from the catalog, for the test harness.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public DataMix MixOverride { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public uint ResolvedMaxPdu => MaxPdu == 0 ? DefaultMaxPdu : MaxPdu;

        [System.Text.Json.Serialization.JsonIgnore]
        public DataMix ResolvedMix => MixOverride ?? DataMixCatalog.Find(Mix);

        /// <summary>The instance ceiling of a mode.</summary>
        public int ResolvedMaxInstances(LoadMode mode)
        {
            if (MaxInstances > 0) return MaxInstances;
            switch (mode)
            {
                case LoadMode.Ramp: return DefaultRampMaxInstances;
                case LoadMode.Soak: return (int)Math.Min(int.MaxValue, Math.Ceiling(SoakRatePerSecond * SoakSeconds));
                default: return DefaultLoadMaxInstances;
            }
        }

        /// <summary>
        /// The most instances the run can send. Exact for a count, otherwise an upper bound: the ceiling, or the rate times
        /// the duration if that is lower.
        /// </summary>
        public int PlannedInstances(LoadMode mode)
        {
            var cap = ResolvedMaxInstances(mode);
            switch (mode)
            {
                case LoadMode.Ramp:
                    return cap;
                case LoadMode.Soak:
                    return (int)Math.Min(cap, Math.Ceiling(SoakRatePerSecond * SoakSeconds));
                default:
                    if (InstanceCount > 0) return Math.Min(InstanceCount, cap);
                    return RatePerSecond > 0 ? (int)Math.Min(cap, Math.Ceiling(RatePerSecond * DurationSeconds)) : cap;
            }
        }

        /// <summary>The planned instances times the estimated size of the mix, uncompressed, which is an upper bound for a compressed mix.</summary>
        public double PlannedMegabytes(LoadMode mode)
        {
            var mix = ResolvedMix;
            return mix == null ? 0 : PlannedInstances(mode) * (double)mix.EstimatedBytesPerObject / 1000000.0;
        }

        /// <summary>The sentence a front-end shows before the run starts.</summary>
        public string DescribePlan(LoadMode mode, string target)
        {
            var count = PlannedInstances(mode);
            var exact = mode == LoadMode.Load && InstanceCount > 0 && InstanceCount <= ResolvedMaxInstances(mode);
            var size = PlannedMegabytes(mode);
            var sizeText = size >= 1000 ? (size / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : size.ToString("0", CultureInfo.InvariantCulture) + " MB";
            var mix = ResolvedMix;
            var plan = $"{(exact ? "Stores" : "Stores up to")} {count.ToString("N0", CultureInfo.InvariantCulture)} new objects ({(mix != null && mix.Syntax.IsEncapsulated ? "at most " : "about ")}{sizeText}, mix {mix?.Id ?? Mix}) on {target}.";

            // Without a rate, a fast system uses up the ceiling before the duration is over, and the measured window shrinks.
            if (mode == LoadMode.Load && InstanceCount == 0 && RatePerSecond <= 0 && DurationSeconds > 0)
            {
                var perSecond = count / DurationSeconds;
                plan += $" If the system takes more than {perSecond.ToString(perSecond < 10 ? "0.#" : "0", CultureInfo.InvariantCulture)} per second, the ceiling ends the run before the {DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s are up.";
            }
            return plan;
        }

        /// <summary>Returns the first problem, or null if the settings can be used for <paramref name="mode"/>.</summary>
        public string Validate(LoadMode mode)
        {
            if (ResolvedMix == null) return $"Unknown data mix '{Mix}'. Mixes: {string.Join(", ", DataMixCatalog.All.Select(m => m.Id))}.";
            if (Associations < 1 || Associations > LargestAssociations) return $"The number of associations must be between 1 and {Int(LargestAssociations)}.";
            if (InstancesPerAssociation < 0) return "Instances per association cannot be negative.";
            if (MaxInstances < 0) return "The instance ceiling cannot be negative.";
            if (WarmUpSeconds < 0) return "The warm-up cannot be negative.";
            if (MaxPdu != 0 && (MaxPdu < 1024 || MaxPdu > 16 * 1024 * 1024)) return "The maximum PDU length must be 0 (the default) or between 1024 and 16777216.";
            if (SampleEvery < 0) return "The sampling for time-to-available cannot be negative.";
            if (SampleEvery > 0 && (PollIntervalSeconds < 0.1 || PollTimeoutSeconds < PollIntervalSeconds)) return "The poll interval must be at least 0.1 s, and the poll timeout at least the interval.";

            switch (mode)
            {
                case LoadMode.Load:
                    if (InstanceCount < 0) return "The instance count cannot be negative.";
                    if (InstanceCount == 0 && DurationSeconds <= 0) return "Give a duration or an instance count.";
                    if (InstanceCount == 0 && WarmUpSeconds >= DurationSeconds) return "The warm-up must be shorter than the duration.";
                    if (RatePerSecond < 0) return "The rate cannot be negative.";
                    break;
                case LoadMode.Ramp:
                    if (RampSteps == null || RampSteps.Count == 0) return "Give at least one ramp step.";
                    if (RampSteps.Any(s => s < 1 || s > LargestAssociations)) return $"Each ramp step must be between 1 and {Int(LargestAssociations)} associations.";
                    for (var i = 1; i < RampSteps.Count; i++)
                    {
                        if (RampSteps[i] <= RampSteps[i - 1]) return "The ramp steps must rise, for example 1,2,4,8.";
                    }
                    if (RampStepSeconds <= 0) return "The step duration must be more than 0.";
                    if (WarmUpSeconds >= RampStepSeconds) return "The warm-up must be shorter than a ramp step.";
                    if (RampMinGainPercent < 0 || RampLatencyFactor <= 1 || RampErrorRisePoints < 0) return "The saturation thresholds are out of range: the gain cannot be negative and the latency factor must be more than 1.";
                    break;
                case LoadMode.Soak:
                    if (SoakSeconds <= 0) return "The soak duration must be more than 0.";
                    if (SoakRatePerSecond <= 0) return "A soak test needs a rate, since it is steady load.";
                    if (SnapshotSeconds < 1) return "The snapshot interval must be at least 1 s.";
                    if (SnapshotSeconds > SoakSeconds) return "The snapshot interval cannot be longer than the soak.";
                    if (SoakLatencyRisePercent <= 0 || SoakThroughputDropPercent <= 0 || SoakThroughputDropPercent >= 100) return "The degradation thresholds are out of range.";
                    break;
            }
            return null;
        }

        /// <summary>Parses 1,2,4,8. Returns null if the text is not such a list.</summary>
        public static List<int> ParseSteps(string text)
        {
            var steps = new List<int>();
            foreach (var part in (text ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
                steps.Add(value);
            }
            return steps.Count == 0 ? null : steps;
        }

        /// <summary>Parses 90, 90s, 30m or 8h as seconds. Returns false if it is none of those.</summary>
        public static bool TryParseDuration(string text, out double seconds)
        {
            seconds = 0;
            var value = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0) return false;
            var factor = 1.0;
            var last = value[value.Length - 1];
            if (last == 's' || last == 'm' || last == 'h')
            {
                factor = last == 'h' ? 3600 : last == 'm' ? 60 : 1;
                value = value.Substring(0, value.Length - 1);
            }
            if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return false;
            seconds = number * factor;
            return true;
        }

        public LoadSettings Clone()
        {
            var copy = (LoadSettings)MemberwiseClone();
            copy.RampSteps = RampSteps?.ToList();
            return copy;
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
