using DCST4.Properties;
using DCST4.ViewModels;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace DCST4
{
    public partial class MainWindow : Window
    {
        public static readonly RoutedUICommand HelpRoutedCommand = new RoutedUICommand("Help", "Help", typeof(MainWindow));

        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            CommandBindings.Add(new CommandBinding(HelpRoutedCommand, (s, e) => ShowHelp()));
            InputBindings.Add(new KeyBinding(HelpRoutedCommand, Key.F1, ModifierKeys.None));

            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Title = $"{AppInfo.DisplayName} v{version}";

            // Keep the newest row and log line in view while a run is going.
            ((INotifyCollectionChanged)_viewModel.Results).CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add) ResultsGrid.ScrollIntoView(e.NewItems[0]);
            };
            ((INotifyCollectionChanged)_viewModel.LogLines).CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && LogList.IsVisible) LogList.ScrollIntoView(e.NewItems[0]);
            };
        }

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
            Settings.Default.Save();
            base.OnClosed(e);
        }

        private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (new SettingsDialog { Owner = this }.ShowDialog() == true) _viewModel.ReloadAdditionalTags();
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
