using DCST4.Core.Performance;
using System.Globalization;

namespace DCST4.ViewModels
{
    /// <summary>
    /// The Performance settings dialog: a copy of the settings of the Performance tab, apart from the test and the mix, so
    /// that Cancel leaves them as they were. Numbers are text here, so that a typo is reported instead of being dropped by
    /// the binding. OK checks the settings of all three tests and only then hands them to <see cref="PerformanceViewModel"/>.
    /// </summary>
    public sealed class PerformanceSettingsViewModel
    {
        private readonly PerformanceViewModel _performance;
        private readonly LoadSettings _copy;

        public PerformanceSettingsViewModel(PerformanceViewModel performance)
        {
            _performance = performance;
            _copy = performance.Settings;
            Associations = Int(_copy.Associations);
            InstancesPerAssociation = Int(_copy.InstancesPerAssociation);
            MaxPdu = _copy.MaxPdu.ToString(CultureInfo.InvariantCulture);
            DurationSeconds = Number(_copy.DurationSeconds);
            InstanceCount = Int(_copy.InstanceCount);
            RatePerSecond = Number(_copy.RatePerSecond);
            RampSteps = string.Join(",", _copy.RampSteps ?? new System.Collections.Generic.List<int>());
            RampStepSeconds = Number(_copy.RampStepSeconds);
            SoakDuration = Duration(_copy.SoakSeconds);
            SoakRatePerSecond = Number(_copy.SoakRatePerSecond);
            SnapshotSeconds = Number(_copy.SnapshotSeconds);
            MaxInstances = Int(_copy.MaxInstances);
            WarmUpSeconds = Number(_copy.WarmUpSeconds);
            SampleEvery = Int(_copy.SampleEvery);
            PollIntervalSeconds = Number(_copy.PollIntervalSeconds);
            PollTimeoutSeconds = Number(_copy.PollTimeoutSeconds);
        }

        public string Associations { get; set; }
        public string InstancesPerAssociation { get; set; }
        public string MaxPdu { get; set; }
        public string DurationSeconds { get; set; }
        public string InstanceCount { get; set; }
        public string RatePerSecond { get; set; }
        public string RampSteps { get; set; }
        public string RampStepSeconds { get; set; }
        public string SoakDuration { get; set; }
        public string SoakRatePerSecond { get; set; }
        public string SnapshotSeconds { get; set; }
        /// <summary>0 is the default of each test.</summary>
        public string MaxInstances { get; set; }
        public string WarmUpSeconds { get; set; }
        public string SampleEvery { get; set; }
        public string PollIntervalSeconds { get; set; }
        public string PollTimeoutSeconds { get; set; }

        /// <summary>The defaults of Max instances, for the text beside the box.</summary>
        public string MaxInstancesDefaults =>
            $"0 is each test's default: {Int(LoadSettings.DefaultLoadMaxInstances)} for Load, {Int(LoadSettings.DefaultRampMaxInstances)} for Ramp, rate x duration for Soak";

        /// <summary>Checks every value and, if all can be used by all three tests, applies them. Returns the first problem, or null when applied.</summary>
        public string Apply()
        {
            string problem = null;
            if (!ReadInt(Associations, "Associations", out var associations, ref problem)
                || !ReadInt(InstancesPerAssociation, "Per association", out var perAssociation, ref problem)
                || !ReadInt(MaxPdu, "Max PDU", out var maxPdu, ref problem)
                || !ReadNumber(DurationSeconds, "Load duration", out var duration, ref problem)
                || !ReadInt(InstanceCount, "Load count", out var count, ref problem)
                || !ReadNumber(RatePerSecond, "Load rate", out var rate, ref problem)
                || !ReadNumber(RampStepSeconds, "Each step", out var stepSeconds, ref problem)
                || !ReadNumber(SoakRatePerSecond, "Soak rate", out var soakRate, ref problem)
                || !ReadNumber(SnapshotSeconds, "Snapshot every", out var snapshot, ref problem)
                || !ReadInt(MaxInstances, "Max instances", out var maxInstances, ref problem)
                || !ReadNumber(WarmUpSeconds, "Warm-up", out var warmUp, ref problem)
                || !ReadInt(SampleEvery, "Time-to-available 1 in", out var sampleEvery, ref problem)
                || !ReadNumber(PollIntervalSeconds, "Time-to-available every", out var pollInterval, ref problem)
                || !ReadNumber(PollTimeoutSeconds, "Time-to-available for up to", out var pollTimeout, ref problem))
            {
                return problem;
            }

            var steps = LoadSettings.ParseSteps(RampSteps);
            if (steps == null) return "Steps must be whole numbers separated by commas, for example 1,2,4,8.";
            if (!LoadSettings.TryParseDuration(SoakDuration, out var soakSeconds)) return "The soak duration must be a number of seconds, or a number followed by s, m or h, for example 90s, 30m or 8h.";

            var settings = _copy.Clone();
            settings.Associations = associations;
            settings.InstancesPerAssociation = perAssociation;
            settings.MaxPdu = (uint)maxPdu;
            settings.DurationSeconds = duration;
            settings.InstanceCount = count;
            settings.RatePerSecond = rate;
            settings.RampSteps = steps;
            settings.RampStepSeconds = stepSeconds;
            settings.SoakSeconds = soakSeconds;
            settings.SoakRatePerSecond = soakRate;
            settings.SnapshotSeconds = snapshot;
            settings.MaxInstances = maxInstances;
            settings.WarmUpSeconds = warmUp;
            settings.SampleEvery = sampleEvery;
            settings.PollIntervalSeconds = pollInterval;
            settings.PollTimeoutSeconds = pollTimeout;

            foreach (var mode in new[] { LoadMode.Load, LoadMode.Ramp, LoadMode.Soak })
            {
                problem = settings.Validate(mode);
                if (problem != null) return mode + ": " + problem;
            }

            _performance.ApplySettings(settings);
            return null;
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Duration(double seconds)
        {
            if (seconds >= 3600 && seconds % 3600 == 0) return (seconds / 3600).ToString("0", CultureInfo.InvariantCulture) + "h";
            if (seconds >= 60 && seconds % 60 == 0) return (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + "m";
            return seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s";
        }

        private static bool ReadInt(string text, string name, out int value, ref string problem)
        {
            if (int.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;
            problem = $"{name} must be a whole number of 0 or more.";
            return false;
        }

        private static bool ReadNumber(string text, string name, out double value, ref string problem)
        {
            if (double.TryParse((text ?? string.Empty).Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return true;
            problem = $"{name} must be a number of 0 or more, with a point for decimals.";
            return false;
        }
    }
}
