using DCST4.Core.Reporting;
using DCST4.Properties;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DCST4.ViewModels
{
    /// <summary>
    /// The Compare runs dialog: which saved runs to compare, in which order, and the threshold. It only collects the choices.
    /// <see cref="MainViewModel.CompareRunsAsync"/> does the work, so that it has the busy state and Cancel like everything else.
    /// The threshold is text here, so that a typo is reported instead of being dropped by the binding.
    /// </summary>
    public sealed class CompareRunsViewModel : INotifyPropertyChanged
    {
        private string _selectedRun;
        private string _thresholdText;

        public CompareRunsViewModel()
        {
            _thresholdText = Text(Settings.Default.CompareThresholdPercent);
            Runs.CollectionChanged += (s, e) => { OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(Hint)); };
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>The JSON files of the runs. The first is the baseline.</summary>
        public ObservableCollection<string> Runs { get; } = new ObservableCollection<string>();

        public string SelectedRun
        {
            get => _selectedRun;
            set { _selectedRun = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelection)); }
        }

        public string ThresholdText
        {
            get => _thresholdText;
            set { _thresholdText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool CanCompare => Runs.Count >= 2;
        public bool HasSelection => SelectedRun != null;

        public string Hint => Runs.Count == 0 ? "Add the result files of two or more runs. The first is the baseline."
            : Runs.Count == 1 ? "Add at least one more run to compare with the baseline."
            : $"{Runs.Count - 1} run(s) will be compared with the baseline, {Path.GetFileNameWithoutExtension(Runs[0])}.";

        /// <summary>After the settings were reset.</summary>
        public void ReloadFromSettings() => ThresholdText = Text(Settings.Default.CompareThresholdPercent);

        /// <summary>
        /// Adds run files, each once. The capability matrix and the files of other kinds that lie beside a run are left out, and
        /// the return value says how many, so that the dialog can tell the user.
        /// </summary>
        public int Add(IEnumerable<string> paths)
        {
            var skipped = 0;
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith("-capability.json", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }
                if (Runs.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase))) continue;
                Runs.Add(path);
            }
            if (SelectedRun == null && Runs.Count > 0) SelectedRun = Runs[Runs.Count - 1];
            return skipped;
        }

        public void RemoveSelected()
        {
            var index = Runs.IndexOf(SelectedRun);
            if (index < 0) return;
            Runs.RemoveAt(index);
            SelectedRun = Runs.Count == 0 ? null : Runs[Math.Min(index, Runs.Count - 1)];
        }

        /// <summary>Moves the selected run one place up (-1) or down (1). The run at the top is the baseline.</summary>
        public void MoveSelected(int direction)
        {
            var index = Runs.IndexOf(SelectedRun);
            var target = index + direction;
            if (index < 0 || target < 0 || target >= Runs.Count) return;
            var run = SelectedRun;
            Runs.Move(index, target);
            SelectedRun = run;
            OnPropertyChanged(nameof(Hint));
        }

        /// <summary>What is wrong with the choices, or null. The threshold is then in <paramref name="thresholdPercent"/>.</summary>
        public string Validate(out double thresholdPercent)
        {
            thresholdPercent = 0;
            if (Runs.Count < 2) return "Add at least two runs. The first is the baseline.";
            var text = (ThresholdText ?? string.Empty).Trim().Replace(',', '.').TrimEnd('%').Trim();
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out thresholdPercent))
            {
                return $"The threshold must be a number of percent, not '{ThresholdText}'.";
            }
            return new ComparisonOptions { ThresholdPercent = thresholdPercent }.Validate();
        }

        private static string Text(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
