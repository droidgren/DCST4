using System.Windows;

namespace DCST4
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            Title = "About " + AppInfo.DisplayName;
            NameText.Text = AppInfo.DisplayName;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => Close();
    }
}
