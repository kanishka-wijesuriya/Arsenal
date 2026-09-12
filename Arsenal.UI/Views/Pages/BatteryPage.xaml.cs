using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class BatteryPage : System.Windows.Controls.UserControl
    {
        public BatteryPage(BatteryViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void ChargeLimitSlider_Commit(object sender, System.Windows.Input.MouseButtonEventArgs e) => CommitLimit();
        private void ChargeLimitSlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e) => CommitLimit();

        private void CommitLimit()
        {
            if (DataContext is BatteryViewModel vm)
                vm.SetLimit(vm.ChargeLimit);
        }
    }
}

