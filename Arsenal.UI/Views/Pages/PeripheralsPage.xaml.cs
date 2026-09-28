using Arsenal.UI.Controls;
using Arsenal.UI.ViewModels;
using System.Windows;

namespace Arsenal.UI.Views.Pages
{
    public partial class PeripheralsPage : System.Windows.Controls.UserControl
    {
        public PeripheralsPage(PeripheralsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // Static event, so held only while the page is on screen.
            Loaded += (_, _) =>
            {
                SettingsGroup.OpenGroupChanged -= OnOpenGroupChanged;
                SettingsGroup.OpenGroupChanged += OnOpenGroupChanged;
            };
            Unloaded += (_, _) => SettingsGroup.OpenGroupChanged -= OnOpenGroupChanged;
        }

        /// <summary>
        /// Reads a device again each time its page is opened, as G-Helper does each time
        /// its window for one opens.
        /// </summary>
        private void OnOpenGroupChanged(SettingsGroup? group)
        {
            if (group?.DataContext is PeripheralViewModel peripheral && IsAncestorOf(group))
                peripheral.Opened();
        }
    }
}
