using DCST4.Core.Performance;
using DCST4.Core.Testing;
using DCST4.Core.Testing.Cases;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DCST4.ViewModels
{
    /// <summary>A mode in the list on the Performance tab.</summary>
    public sealed class LoadModeChoice
    {
        public LoadModeChoice(LoadMode mode, string name)
        {
            Mode = mode;
            Name = name;
        }

        public LoadMode Mode { get; }
        public string Name { get; }
        public override string ToString() => Name;
    }

    /// <summary>One row of the summary table after a run: the whole load, a ramp step or a soak window.</summary>
    public sealed class LoadSummaryRow
    {
        public string Label { get; set; }
        public int Associations { get; set; }
        public long Stored { get; set; }
        public string StoredPerSecond { get; set; }
        public string MegabytesPerSecond { get; set; }
        public string LatencyP50 { get; set; }
        public string LatencyP95 { get; set; }
        public string LatencyP99 { get; set; }
        public string LatencyMax { get; set; }
        public string Setup { get; set; }
        public long NotStored { get; set; }
        public string Note { get; set; }
        /// <summary>The ramp step where saturation was found, or a window that counts as degraded.</summary>
        public bool Marked { get; set; }

        internal static string Ms(double value) => value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);
        internal static string Rate(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

        internal static LoadSummaryRow From(string label, int associations, LoadSummary summary) => new LoadSummaryRow
        {
            Label = label,
            Associations = associations,
            Stored = summary.Stored,
            StoredPerSecond = Rate(summary.StoredPerSecond),
            MegabytesPerSecond = summary.MegabytesPerSecond.ToString("0.00", CultureInfo.InvariantCulture),
            LatencyP50 = Ms(summary.Latency?.P50Ms ?? 0),
            LatencyP95 = Ms(summary.Latency?.P95Ms ?? 0),
            LatencyP99 = Ms(summary.Latency?.P99Ms ?? 0),
            LatencyMax = Ms(summary.Latency?.MaxMs ?? 0),
            Setup = summary.AssociationSetup != null && summary.AssociationSetup.Count > 0 ? Ms(summary.AssociationSetup.P50Ms) : string.Empty,
            NotStored = summary.Errors?.NotStored ?? 0,
        };
    }

    /// <summary>
    /// What the Performance tab shows: the settings of the three load tests, the plan, the live chart and the summary
    /// afterwards. Running goes through <see cref="MainViewModel"/>, which owns the busy state and Cancel.
    /// </summary>
    public sealed class PerformanceViewModel : INotifyPropertyChanged
    {
        private readonly ConcurrentQueue<LoadSecond> _incoming = new ConcurrentQueue<LoadSecond>();
        private readonly List<LoadSecond> _series = new List<LoadSecond>();
        private LoadSettings _settings;
        private LoadModeChoice _mode;
        private int _version;
        private string _verdict;
        private string _availability;
        private string _files;
        private Func<string> _target = () => "the destination";

        public PerformanceViewModel(string savedSettings = null, string savedMode = null)
        {
            _settings = Parse(savedSettings) ?? new LoadSettings();
            _mode = Modes.FirstOrDefault(m => string.Equals(m.Mode.ToString(), savedMode, StringComparison.OrdinalIgnoreCase)) ?? Modes[0];
        }

        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>Raised when a setting changes, so the main view model can save it.</summary>
        public event Action SettingsChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void Changed([CallerMemberName] string name = null)
        {
            OnPropertyChanged(name);
            OnPropertyChanged(nameof(PlanText));
            // The default ceiling depends on the test, and for the soak on its rate and duration.
            if (name != nameof(MaxInstances)) OnPropertyChanged(nameof(MaxInstances));
            OnPropertyChanged(nameof(MaxInstancesNote));
            SettingsChanged?.Invoke();
        }

        public IReadOnlyList<LoadModeChoice> Modes { get; } = new[]
        {
            new LoadModeChoice(LoadMode.Load, "Load: throughput and latency"),
            new LoadModeChoice(LoadMode.Ramp, "Ramp: 1, 2, 4 ... associations until saturated"),
            new LoadModeChoice(LoadMode.Soak, "Soak: steady load for a long time"),
        };

        public IReadOnlyList<DataMix> Mixes => DataMixCatalog.All;

        public LoadModeChoice SelectedMode
        {
            get => _mode;
            set
            {
                _mode = value ?? Modes[0];
                Changed();
                OnPropertyChanged(nameof(IsLoad));
                OnPropertyChanged(nameof(IsRamp));
                OnPropertyChanged(nameof(IsSoak));
            }
        }

        public bool IsLoad => _mode.Mode == LoadMode.Load;
        public bool IsRamp => _mode.Mode == LoadMode.Ramp;
        public bool IsSoak => _mode.Mode == LoadMode.Soak;

        /// <summary>The test of the selected mode, from the catalog.</summary>
        public ITestCase SelectedTest => TestCatalog.All.OfType<LoadTestBase>().First(t => t.Mode == _mode.Mode);

        public DataMix SelectedMix
        {
            get => DataMixCatalog.Find(_settings.Mix) ?? DataMixCatalog.All[0];
            set { _settings.Mix = value?.Id ?? LoadSettings.DefaultMix; Changed(); }
        }

        public int Associations { get => _settings.Associations; set { _settings.Associations = value; Changed(); } }
        public int InstancesPerAssociation { get => _settings.InstancesPerAssociation; set { _settings.InstancesPerAssociation = value; Changed(); } }
        public double DurationSeconds { get => _settings.DurationSeconds; set { _settings.DurationSeconds = value; Changed(); } }
        public int InstanceCount { get => _settings.InstanceCount; set { _settings.InstanceCount = value; Changed(); } }
        public double RatePerSecond { get => _settings.RatePerSecond; set { _settings.RatePerSecond = value; Changed(); } }
        public uint MaxPdu { get => _settings.MaxPdu; set { _settings.MaxPdu = value; Changed(); } }
        public double WarmUpSeconds { get => _settings.WarmUpSeconds; set { _settings.WarmUpSeconds = value; Changed(); } }
        /// <summary>
        /// The ceiling that applies to the selected test: the one you set, or the test's default while you have set none.
        /// Setting 0 goes back to the defaults. A value you set applies to all three tests.
        /// </summary>
        public int MaxInstances { get => _settings.ResolvedMaxInstances(_mode.Mode); set { _settings.MaxInstances = Math.Max(0, value); Changed(); } }

        /// <summary>Says whether <see cref="MaxInstances"/> is the default or set by you.</summary>
        public string MaxInstancesNote => _settings.MaxInstances == 0
            ? "(the default for this test)"
            : "(set by you, for all three tests. 0 goes back to the default)";
        public double RampStepSeconds { get => _settings.RampStepSeconds; set { _settings.RampStepSeconds = value; Changed(); } }
        public double SoakRatePerSecond { get => _settings.SoakRatePerSecond; set { _settings.SoakRatePerSecond = value; Changed(); } }
        public double SnapshotSeconds { get => _settings.SnapshotSeconds; set { _settings.SnapshotSeconds = value; Changed(); } }
        public int SampleEvery { get => _settings.SampleEvery; set { _settings.SampleEvery = value; Changed(); } }
        public double PollIntervalSeconds { get => _settings.PollIntervalSeconds; set { _settings.PollIntervalSeconds = value; Changed(); } }
        public double PollTimeoutSeconds { get => _settings.PollTimeoutSeconds; set { _settings.PollTimeoutSeconds = value; Changed(); } }

        /// <summary>1,2,4,8. Text that is not such a list is kept as it is, and the plan says what is wrong.</summary>
        public string RampStepsText
        {
            get => _rampStepsText ?? string.Join(",", _settings.RampSteps ?? new List<int>());
            set
            {
                _rampStepsText = value;
                var steps = LoadSettings.ParseSteps(value);
                _settings.RampSteps = steps ?? new List<int>();
                Changed();
            }
        }
        private string _rampStepsText;

        /// <summary>The soak's length as 90s, 30m or 8h.</summary>
        public string SoakDurationText
        {
            get => _soakText ?? Duration(_settings.SoakSeconds);
            set
            {
                _soakText = value;
                _settings.SoakSeconds = LoadSettings.TryParseDuration(value, out var seconds) ? seconds : 0;
                Changed();
            }
        }
        private string _soakText;

        /// <summary>Where the run would go, for the plan. Set by the main view model.</summary>
        public void SetTarget(Func<string> target)
        {
            _target = target ?? (() => "the destination");
            OnPropertyChanged(nameof(PlanText));
        }

        public void RefreshPlan() => OnPropertyChanged(nameof(PlanText));

        public string Problem => _settings.Validate(_mode.Mode);

        /// <summary>The planned total, or what is wrong with the settings.</summary>
        public string PlanText => Problem ?? _settings.DescribePlan(_mode.Mode, _target());

        public LoadSettings Settings => _settings.Clone();

        public string SaveSettings() => JsonSerializer.Serialize(_settings);

        private static LoadSettings Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<LoadSettings>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ------------------------------------------------------------ the live chart

        /// <summary>What the chart draws. Only changed on the UI thread, by <see cref="Drain"/>.</summary>
        public IReadOnlyList<LoadSecond> Series => _series;

        /// <summary>Changes after each drain, so that the chart redraws.</summary>
        public int SeriesVersion
        {
            get => _version;
            private set { _version = value; OnPropertyChanged(); }
        }

        /// <summary>From the test runner's thread.</summary>
        public void Enqueue(LoadSecond second) => _incoming.Enqueue(second);

        /// <summary>Called on the UI thread by a timer once a second. Returns whether anything was added.</summary>
        public bool Drain()
        {
            var added = false;
            while (_incoming.TryDequeue(out var second))
            {
                _series.Add(second);
                added = true;
            }
            if (added) SeriesVersion++;
            return added;
        }

        private LoadSettings _runSettings;
        private LoadMode _runMode;

        /// <summary>How far a run has come, from 0 to 1, from the seconds the chart has received.</summary>
        public double Progress
        {
            get
            {
                if (_runSettings == null || _series.Count == 0) return 0;
                var s = _runSettings;
                if (_runMode == LoadMode.Load && s.InstanceCount > 0)
                {
                    // A count: images that got an answer, or none, against the count, or the ceiling if that is lower.
                    var target = Math.Min(s.InstanceCount, s.ResolvedMaxInstances(LoadMode.Load));
                    return Math.Min(1, _series.Sum(r => r.Attempts) / (double)Math.Max(1, target));
                }

                // A duration: seconds run against the seconds planned. The ramp's steps follow each other in the series.
                var planned = _runMode == LoadMode.Ramp ? s.RampSteps.Count * s.RampStepSeconds
                    : _runMode == LoadMode.Soak ? s.SoakSeconds
                    : s.DurationSeconds;
                return planned <= 0 ? 0 : Math.Min(1, (_series[_series.Count - 1].Second + 1) / planned);
            }
        }

        /// <summary>Remembers what the run that starts now was asked to do, for <see cref="Progress"/>.</summary>
        public void StartRun(LoadSettings settings, LoadMode mode)
        {
            _runSettings = settings;
            _runMode = mode;
        }

        public void Clear()
        {
            while (_incoming.TryDequeue(out _)) { }
            _series.Clear();
            Rows.Clear();
            Verdict = null;
            Availability = null;
            Files = null;
            SeriesVersion++;
        }

        // ------------------------------------------------------------ after the run

        public ObservableCollection<LoadSummaryRow> Rows { get; } = new ObservableCollection<LoadSummaryRow>();

        public string Verdict { get => _verdict; private set { _verdict = value; OnPropertyChanged(); } }
        public string Availability { get => _availability; private set { _availability = value; OnPropertyChanged(); } }
        public string Files { get => _files; set { _files = value; OnPropertyChanged(); } }

        /// <summary>Fills the summary table from a finished test.</summary>
        public void Show(TestResult result)
        {
            Drain();
            Rows.Clear();
            var p = result.Performance;
            Verdict = result.Result + ": " + result.Explanation;
            if (p == null) return;

            if (p.Steps != null)
            {
                foreach (var step in p.Steps)
                {
                    var row = LoadSummaryRow.From("Step " + step.Step.ToString(CultureInfo.InvariantCulture), step.Associations, step.Summary);
                    row.Marked = step.Saturated;
                    if (step.Saturated) row.Note = "saturated: " + p.Saturation.Reason;
                    if (p.Saturation != null && step.Step == p.Saturation.BestStep) row.Note = string.IsNullOrEmpty(row.Note) ? "highest throughput" : row.Note + ", highest throughput";
                    Rows.Add(row);
                }
                if (p.Saturation != null) Verdict += " " + p.Saturation.Description;
            }
            else if (p.Windows != null)
            {
                Rows.Add(Whole(p, "Whole soak"));
                foreach (var window in p.Windows)
                {
                    Rows.Add(LoadSummaryRow.From($"Window {window.Window.ToString(CultureInfo.InvariantCulture)} ({window.StartSecond.ToString(CultureInfo.InvariantCulture)}-{window.EndSecond.ToString(CultureInfo.InvariantCulture)} s)", p.Settings.Associations, window.Summary));
                }
                if (p.Degradation != null) Verdict += " " + p.Degradation.Description;
            }
            else if (p.Summary != null)
            {
                Rows.Add(Whole(p, "Whole run"));
            }

            var tta = p.TimeToAvailable;
            Availability = tta == null ? null
                : tta.QuerySupported == false ? "Time-to-available: " + tta.Note
                : $"Time-to-available, 1 in {tta.SampleEvery.ToString(CultureInfo.InvariantCulture)} polled every {tta.IntervalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s: {tta.Found.ToString(CultureInfo.InvariantCulture)} of {tta.Sampled.ToString(CultureInfo.InvariantCulture)} found, p50 {LoadSummaryRow.Ms(tta.Available?.P50Ms ?? 0)} ms, p95 {LoadSummaryRow.Ms(tta.Available?.P95Ms ?? 0)} ms, max {LoadSummaryRow.Ms(tta.Available?.MaxMs ?? 0)} ms, {tta.NeverFound.ToString(CultureInfo.InvariantCulture)} never found within {tta.TimeoutSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s";
        }

        private static LoadSummaryRow Whole(PerformanceResult p, string label)
        {
            var row = LoadSummaryRow.From(label, p.Settings.Associations, p.Summary);
            row.Note = p.EndedByCeiling ? "ended early by the instance ceiling: a short window, see above"
                : p.StoppedBy == "count" ? "stopped at the instance count"
                : "stopped by " + p.StoppedBy;
            row.Marked = p.EndedByCeiling;
            return row;
        }

        private static string Duration(double seconds)
        {
            if (seconds >= 3600 && seconds % 3600 == 0) return (seconds / 3600).ToString("0", CultureInfo.InvariantCulture) + "h";
            if (seconds >= 60 && seconds % 60 == 0) return (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + "m";
            return seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s";
        }
    }
}
