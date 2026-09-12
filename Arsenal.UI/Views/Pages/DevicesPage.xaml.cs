using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class DevicesPage : System.Windows.Controls.UserControl
    {
        public DevicesPage(DevicesViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

