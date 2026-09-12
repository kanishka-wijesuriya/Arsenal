using Arsenal.UI.ViewModels;
using System.Windows.Controls;

namespace Arsenal.UI.Views.Pages
{
    public partial class HomePage : System.Windows.Controls.UserControl
    {
        public HomePage(HomeViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        // The limit reaches the embedded controller when the drag ends, not on every
        // value the thumb passes over. Same handling as the Battery page.
        private void ChargeLimitSlider_Commit(object sender, System.Windows.Input.MouseButtonEventArgs e) => CommitLimit();
        private void ChargeLimitSlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e) => CommitLimit();

        private void CommitLimit()
        {
            if (DataContext is HomeViewModel vm)
                vm.SetChargeLimit(vm.ChargeLimit);
        }
    }
}

