using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DCST4
{
    /// <summary>
    /// A yes or no question whose lines are never wrapped, unlike a MessageBox, which breaks a long list of tests into a hard
    /// to read column. The window is as wide as the longest line, up to most of the screen.
    /// </summary>
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog(string message, string title = "Confirm", string yesText = null, string noText = null)
        {
            InitializeComponent();
            Title = title;
            if (yesText != null) YesButton.Content = yesText;
            if (noText != null) NoButton.Content = noText;
            MessageText.Text = message;
            MessageScroller.MaxWidth = SystemParameters.WorkArea.Width * 0.85;
            MessageScroller.MaxHeight = SystemParameters.WorkArea.Height * 0.7;
            QuestionIcon.Source = Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Question.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        /// <summary>Asks over the main window, or over the active one when a dialog is open. True for Yes.</summary>
        /// <param name="yesText">The default button, instead of Yes.</param>
        /// <param name="noText">The other button, which Esc and closing the window also mean, instead of No.</param>
        public static bool Ask(string message, string title = "Confirm", string yesText = null, string noText = null)
        {
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
            var dialog = new ConfirmDialog(message, title, yesText, noText);
            if (owner != null && owner.IsVisible) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dialog.ShowDialog() == true;
        }

        private void YesButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
