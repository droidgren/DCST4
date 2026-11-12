using DCST4.ViewModels;
using System.Windows;

namespace DCST4
{
    /// <summary>The settings of the Tests tab, apart from the local SCP AET and port. Nothing changes until OK.</summary>
    public partial class TestSettingsDialog : Window
    {
        private readonly TestSettingsViewModel _settings;

        public TestSettingsDialog(TestSettingsViewModel settings)
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
