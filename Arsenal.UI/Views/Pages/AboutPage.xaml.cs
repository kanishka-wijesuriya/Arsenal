using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class AboutPage : System.Windows.Controls.UserControl
    {
        public AboutPage(AboutViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

