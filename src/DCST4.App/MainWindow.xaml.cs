using DCST4.Infrastructure;
using DCST4.Properties;
using DCST4.ViewModels;
using Serilog;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DCST4
{
    public partial class MainWindow : Window
    {
        public static readonly RoutedUICommand HelpRoutedCommand = new RoutedUICommand("Help", "Help", typeof(MainWindow));

        private readonly MainViewModel _viewModel;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            CommandBindings.Add(new CommandBinding(HelpRoutedCommand, (s, e) => ShowHelp()));
            InputBindings.Add(new KeyBinding(HelpRoutedCommand, Key.F1, ModifierKeys.None));

            Title = $"{AppInfo.DisplayName} v{AppInfo.CurrentVersion}";
            ContentRendered += ShowUpdateWindowIfNewVersion;

            // Keep the newest row and log line in view while a run is going.
            ((INotifyCollectionChanged)_viewModel.Results).CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add) ResultsGrid.ScrollIntoView(e.NewItems[0]);
            };
            ((INotifyCollectionChanged)_viewModel.LogLines).CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && LogList.IsVisible) LogList.ScrollIntoView(e.NewItems[0]);
            };

            _viewModel.TestSuite.Capabilities.ColumnsChanged += RebuildCapabilityColumns;
        }

        /// <summary>One column per transfer syntax, so they follow the matrix rather than being written in the XAML.</summary>
        private void RebuildCapabilityColumns()
        {
            CapabilityGrid.Columns.Clear();
            CapabilityGrid.Columns.Add(new DataGridTextColumn { Header = "SOP class", Binding = new Binding(nameof(CapabilityRowView.SopClass)), Width = 310 });

            var columns = _viewModel.TestSuite.Capabilities.Columns;
            for (var i = 0; i < columns.Count; i++)
            {
                var text = new FrameworkElementFactory(typeof(TextBlock));
                text.SetBinding(TextBlock.TextProperty, new Binding($"Cells[{i}].{nameof(CapabilityCellView.Code)}"));
                text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

                var cell = new FrameworkElementFactory(typeof(Border));
                cell.SetBinding(Border.BackgroundProperty, new Binding($"Cells[{i}].{nameof(CapabilityCellView.Background)}"));
                cell.SetBinding(ToolTipProperty, new Binding($"Cells[{i}].{nameof(CapabilityCellView.ToolTip)}"));
                cell.AppendChild(text);

                CapabilityGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = new TextBlock { Text = columns[i].ShortName, ToolTip = columns[i].Name, LayoutTransform = new RotateTransform(-90) },
                    CellTemplate = new DataTemplate { VisualTree = cell },
                    Width = 34,
                });
            }
        }

        private void SaveCapabilityJson_Click(object sender, RoutedEventArgs e) => SaveCapabilityMatrix("JSON files (*.json)|*.json", ".json", false);

        private void SaveCapabilityCsv_Click(object sender, RoutedEventArgs e) => SaveCapabilityMatrix("CSV files (*.csv)|*.csv", ".csv", true);

        private void SaveCapabilityMatrix(string filter, string extension, bool asCsv)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, DefaultExt = extension, FileName = "capability-matrix" + extension };
            if (dialog.ShowDialog(this) == true) _viewModel.SaveCapabilityMatrix(dialog.FileName, asCsv);
        }

        private void LoadClaimedCapabilities_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", Title = "Select what the vendor claims" };
            if (dialog.ShowDialog(this) == true) _viewModel.LoadClaimedCapabilities(dialog.FileName);
        }

        private void ClearClaimedCapabilities_Click(object sender, RoutedEventArgs e) => _viewModel.TestSuite.Capabilities.SetClaimed(null);

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_viewModel.IsBusy && _viewModel.CancelCommand.CanExecute(null))
            {
                _viewModel.CancelCommand.Execute(null);
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _closing.Cancel();
            Settings.Default.Save();
            base.OnClosed(e);
        }

        /// <summary>Only a new version is ever shown. Anything that goes wrong here stays in the log.</summary>
        private async void ShowUpdateWindowIfNewVersion(object sender, EventArgs e)
        {
            try
            {
                var update = await StartupUpdateCheck.FindNewVersionAsync(
                    Settings.Default.ShowUpdateWindowAtStartup, new UpdateChecker(AppInfo.CurrentVersion), _closing.Token);
                if (update == null || _closing.IsCancellationRequested) return;

                new UpdateAvailableWindow(update, AppInfo.CurrentVersion) { Owner = this }.Show();
            }
            catch (Exception ex)
            {
                Log.ForContext<MainWindow>().Warning(ex, "Could not show the new version window");
            }
        }

        private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog { Owner = this };
            if (dialog.ShowDialog() == true) _viewModel.ReloadAdditionalTags();
            else if (dialog.ResetRequested) _viewModel.ResetSettings();
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this, DataContext = new AboutViewModel() }.ShowDialog();
        }

        private void HelpMenuItem_Click(object sender, RoutedEventArgs e) => ShowHelp();

        private void ShowHelp()
        {
            new HelpWindow { Owner = this }.Show();
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

        private void OpenLogDirectoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(App.LogDirectory)) Directory.CreateDirectory(App.LogDirectory);
                Process.Start(new ProcessStartInfo(App.LogDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to open log directory:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TlsOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TlsOptionsDialog(_viewModel.TlsIgnoreCertErrors, _viewModel.TlsClientCertThumbprint) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _viewModel.TlsIgnoreCertErrors = dialog.IgnoreCertErrors;
                _viewModel.TlsClientCertThumbprint = dialog.ClientCertThumbprint;
            }
        }

        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select the folder for the generated DICOM files",
                SelectedPath = _viewModel.SaveFolder,
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _viewModel.SaveFolder = dialog.SelectedPath;
                }
            }
        }

        private void BrowseTestResultsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select the folder for test results",
                SelectedPath = _viewModel.TestSuite.ResultsFolder,
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _viewModel.TestSuite.ResultsFolder = dialog.SelectedPath;
                }
            }
        }

        private void OpenTestResultsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = _viewModel.TestSuite.ResultsFolder.Trim();
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                MessageBox.Show(this, "Failed to open the results folder:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveTargetProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the destination as a target profile",
                Filter = "Target profile (*.json)|*.json",
                DefaultExt = ".json",
                FileName = "target-profile.json",
            };
            if (dialog.ShowDialog(this) == true) _viewModel.SaveTargetProfile(dialog.FileName);
        }

        private void CopyLogButton_Click(object sender, RoutedEventArgs e)
        {
            var lines = LogList.SelectedItems.Count > 0
                ? _viewModel.LogLines.Where(LogList.SelectedItems.Contains)
                : _viewModel.LogLines;
            var text = string.Join(Environment.NewLine, lines);
            if (text.Length > 0) Clipboard.SetText(text);
        }
    }
}
