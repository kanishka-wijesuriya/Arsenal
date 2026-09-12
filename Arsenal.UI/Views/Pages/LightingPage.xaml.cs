using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class LightingPage : System.Windows.Controls.UserControl
    {
        public LightingPage(LightingViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

