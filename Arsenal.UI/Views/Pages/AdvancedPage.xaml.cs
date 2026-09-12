using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class AdvancedPage : System.Windows.Controls.UserControl
    {
        public AdvancedPage(AdvancedViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

