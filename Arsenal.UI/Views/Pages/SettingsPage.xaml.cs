using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class SettingsPage : System.Windows.Controls.UserControl
    {
        public SettingsPage(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm) vm.SavePreferences();
        }
    }
}

