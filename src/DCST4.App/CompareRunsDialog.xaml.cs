using DCST4.ViewModels;
using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace DCST4
{
    /// <summary>
    /// Chooses the saved runs to compare. The dialog does no work itself: it closes with what was asked for, and the main
    /// window hands that to <see cref="MainViewModel"/>.
    /// </summary>
    public partial class CompareRunsDialog : Window
    {
        private readonly CompareRunsViewModel _choices;
        private readonly string _resultsFolder;

        public CompareRunsDialog(CompareRunsViewModel choices, string resultsFolder)
        {
            InitializeComponent();
            _choices = choices;
            _resultsFolder = resultsFolder;
            DataContext = choices;
        }

        /// <summary>Set when Compare was clicked: the runs, the first being the baseline.</summary>
        public IReadOnlyList<string> RunsToCompare { get; private set; }
        public double ThresholdPercent { get; private set; }
        /// <summary>Set when Open report was clicked.</summary>
        public string RunToOpen { get; private set; }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose the result files of saved runs",
                Filter = "DCST4 runs (DCST4-run-*.json)|DCST4-run-*.json|JSON files (*.json)|*.json",
                Multiselect = true,
                CheckFileExists = true,
            };
            if (!string.IsNullOrWhiteSpace(_resultsFolder) && Directory.Exists(_resultsFolder)) dialog.InitialDirectory = _resultsFolder;
            if (dialog.ShowDialog(this) != true) return;

            var skipped = _choices.Add(dialog.FileNames.OrderBy(f => f, System.StringComparer.OrdinalIgnoreCase));
            if (skipped > 0)
            {
                MessageBox.Show(this, $"{skipped} of the chosen files are not result files of a run and were left out. A run is the DCST4-run-<time>.json file, not the -capability.json beside it.",
                    "Compare runs", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e) => _choices.RemoveSelected();
        private void MoveUpButton_Click(object sender, RoutedEventArgs e) => _choices.MoveSelected(-1);
        private void MoveDownButton_Click(object sender, RoutedEventArgs e) => _choices.MoveSelected(1);

        private void CompareButton_Click(object sender, RoutedEventArgs e)
        {
            var problem = _choices.Validate(out var threshold);
            if (problem != null)
            {
                MessageBox.Show(this, problem, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RunsToCompare = _choices.Runs.ToList();
            ThresholdPercent = threshold;
            DialogResult = true;
        }

        private void OpenReportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_choices.SelectedRun == null) return;
            RunToOpen = _choices.SelectedRun;
            DialogResult = true;
        }
    }
}
