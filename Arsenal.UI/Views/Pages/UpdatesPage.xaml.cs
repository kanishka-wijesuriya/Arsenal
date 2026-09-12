using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class UpdatesPage : System.Windows.Controls.UserControl
    {
        private bool _hasScanned;

        public UpdatesPage(UpdatesViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Loaded += async (_, _) =>
            {
                if (_hasScanned || viewModel.AsusUpdates.Count > 0) return;
                _hasScanned = true;
                await viewModel.CheckUpdates();
            };
        }
    }
}

