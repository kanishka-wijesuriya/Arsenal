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
            Unloaded += (_, _) =>
            {
                SettingsGroup.OpenGroupChanged -= OnOpenGroupChanged;
                SetOpen(null);
            };
        }

        private PeripheralViewModel? _open;

        /// <summary>
        /// Reads a device again each time its page is opened, as G-Helper does each time
        /// its window for one opens.
        /// </summary>
        private void OnOpenGroupChanged(SettingsGroup? group)
        {
            SetOpen(group?.DataContext is PeripheralViewModel peripheral && IsAncestorOf(group) ? peripheral : null);
        }

        private void SetOpen(PeripheralViewModel? peripheral)
        {
            if (ReferenceEquals(peripheral, _open)) return;
            _open?.Closed();
            _open = peripheral;
            _open?.Opened();
        }
    }
}
