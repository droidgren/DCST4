using System.Windows;

namespace DCST4
{
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
            Title = AppInfo.DisplayName + " Help";
        }
    }
}
