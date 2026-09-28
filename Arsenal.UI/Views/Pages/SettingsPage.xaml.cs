using Arsenal.UI.ViewModels;

namespace Arsenal.UI.Views.Pages
{
    public partial class SettingsPage : System.Windows.Controls.UserControl
    {
        public SettingsPage(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

#if ARSENAL_SIMULATOR
            // Added here rather than declared in the XAML, which is compiled into every
            // build: the simulator exists only in Debug and test builds.
            PageBody.Children.Add(new Simulation.SimulatorPanel());
#endif
        }
    }
}
