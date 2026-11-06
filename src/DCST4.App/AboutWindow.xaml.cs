using Serilog;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace DCST4
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            Title = "About " + AppInfo.DisplayName;
            NameText.Text = AppInfo.DisplayName;
            ProjectLink.NavigateUri = new Uri(AppInfo.ProjectUrl);
            ProjectLinkText.Text = AppInfo.ProjectUrl;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => Close();

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                // On .NET Framework this opens the URL in the default browser.
                Process.Start(e.Uri.AbsoluteUri);
            }
            catch (Exception ex)
            {
                Log.ForContext<AboutWindow>().Warning(ex, "Could not open {url}", e.Uri);
                MessageBox.Show(this, "Could not open the browser:\n" + ex.Message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            e.Handled = true;
        }
    }
}
