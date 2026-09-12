using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class AutomationPage : System.Windows.Controls.UserControl
    {
        public AutomationPage(AutomationViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

