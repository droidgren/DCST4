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
        public ConfirmDialog(string message, string title = "Confirm")
        {
            InitializeComponent();
            Title = title;
            MessageText.Text = message;
            MessageScroller.MaxWidth = SystemParameters.WorkArea.Width * 0.85;
            MessageScroller.MaxHeight = SystemParameters.WorkArea.Height * 0.7;
            QuestionIcon.Source = Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Question.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        /// <summary>Asks over the main window, or over the active one when a dialog is open. True for Yes.</summary>
        public static bool Ask(string message, string title = "Confirm")
        {
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
            var dialog = new ConfirmDialog(message, title);
            if (owner != null && owner.IsVisible) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dialog.ShowDialog() == true;
        }

        private void YesButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
