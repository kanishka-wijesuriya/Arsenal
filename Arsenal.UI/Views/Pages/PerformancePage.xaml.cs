using Arsenal.UI.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class PerformancePage : System.Windows.Controls.UserControl
    {
        public PerformancePage(PerformanceViewModel viewModel, bool compactEditor = false)
        {
            InitializeComponent();
            DataContext = viewModel;

            if (compactEditor)
            {
                PageHeader.Visibility = Visibility.Collapsed;
                ProfileGroup.Visibility = Visibility.Collapsed;
            }
        }
    }
}

