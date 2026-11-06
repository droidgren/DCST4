using DCST4.Properties;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DCST4
{
    public partial class SettingsDialog : Window
    {
        public SettingsDialog()
        {
            InitializeComponent();
            var s = Settings.Default;
            ConnectionTimeoutBox.Text = s.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            AssociationTimeoutBox.Text = s.AssociationTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ResponseTimeoutBox.Text = s.ResponseTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            LogDimseDatasets.IsChecked = s.Client_LogDimseDatasets;
            LogDataPDUs.IsChecked = s.Client_LogDataPDUs;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead(ConnectionTimeoutBox, "Connection timeout", 1, 3600, out var connection)) return;
            if (!TryRead(AssociationTimeoutBox, "Association timeout", 1, 3600, out var association)) return;
            if (!TryRead(ResponseTimeoutBox, "Response timeout", 0, 86400, out var response)) return;

            var s = Settings.Default;
            s.ConnectionTimeoutSeconds = connection;
            s.AssociationTimeoutSeconds = association;
            s.ResponseTimeoutSeconds = response;
            s.Client_LogDimseDatasets = LogDimseDatasets.IsChecked == true;
            s.Client_LogDataPDUs = LogDataPDUs.IsChecked == true;
            s.Save();
            DialogResult = true;
        }

        private bool TryRead(TextBox box, string name, int min, int max, out int value)
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
            {
                return true;
            }
            MessageBox.Show(this, $"{name} must be a whole number between {min} and {max}.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            box.Focus();
            return false;
        }
    }
}
