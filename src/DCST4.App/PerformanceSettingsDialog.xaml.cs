using DCST4.ViewModels;
using System.Windows;

namespace DCST4
{
    /// <summary>The settings of the Performance tab, apart from the test and the mix. Nothing changes until OK.</summary>
    public partial class PerformanceSettingsDialog : Window
    {
        private readonly PerformanceSettingsViewModel _settings;

        public PerformanceSettingsDialog(PerformanceSettingsViewModel settings)
        {
            InitializeComponent();
            _settings = settings;
            DataContext = settings;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var problem = _settings.Apply();
            if (problem != null)
            {
                MessageBox.Show(this, problem, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        }
    }
}
