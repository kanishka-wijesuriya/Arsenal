using System.Windows;

namespace Arsenal.UI.Simulation;

public partial class SimulatorPanel : System.Windows.Controls.UserControl
{
    private readonly SimulatorViewModel _viewModel = new();

    public SimulatorPanel()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // The action list listens to a static event. Only while on screen, so a page
        // released to the tray is not held alive by it.
        Loaded += (_, _) => _viewModel.Attach();
        Unloaded += (_, _) => _viewModel.Detach();
    }
}
