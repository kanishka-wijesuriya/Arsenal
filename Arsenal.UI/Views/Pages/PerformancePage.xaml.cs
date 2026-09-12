using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class PerformancePage : System.Windows.Controls.UserControl
    {
        public PerformancePage(PerformanceViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}

