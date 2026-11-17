using DCST4.Core.Pipeline;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DCST4
{
    /// <summary>Shows the image preview of the Exam tab. One window is reused, so previews do not pile up.</summary>
    public partial class PreviewWindow : Window
    {
        private static PreviewWindow _open;

        public PreviewWindow()
        {
            InitializeComponent();
            Closed += (s, e) => { if (ReferenceEquals(_open, this)) _open = null; };
        }

        /// <summary>Shows <paramref name="image"/> in the open preview window, or in a new one.</summary>
        public static void ShowImage(PreviewImage image, Window owner)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            var window = _open;
            if (window == null)
            {
                window = new PreviewWindow();
                if (owner != null && owner.IsLoaded) window.Owner = owner;
                _open = window;
            }
            window.SetImage(image);
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        public void SetImage(PreviewImage image)
        {
            PreviewImageControl.Source = ToBitmap(image);
            DescriptionText.Text = image.Description;
            NoteText.Text = image.Note ?? string.Empty;
            NoteText.Visibility = image.Note == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public static BitmapSource ToBitmap(PreviewImage image)
        {
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
