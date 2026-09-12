using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class DisplayPage : System.Windows.Controls.UserControl
    {
        public DisplayPage(DisplayViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

