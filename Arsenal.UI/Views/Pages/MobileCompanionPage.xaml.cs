using Arsenal.UI.ViewModels;

namespace Arsenal.UI.Views.Pages;

public partial class MobileCompanionPage : System.Windows.Controls.UserControl
{
    public MobileCompanionPage(MobileCompanionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.SetActive(true);
        Unloaded += (_, _) => viewModel.SetActive(false);
    }
}
