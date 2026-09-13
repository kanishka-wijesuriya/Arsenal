using Arsenal.UI.ViewModels;

namespace Arsenal.UI.Views.Pages
{
    public partial class SettingsPage : System.Windows.Controls.UserControl
    {
        public SettingsPage(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
