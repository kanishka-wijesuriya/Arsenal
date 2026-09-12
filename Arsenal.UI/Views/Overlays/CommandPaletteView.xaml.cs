using Arsenal.UI.ViewModels;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Arsenal.UI.Views.Overlays
{
    public partial class CommandPaletteView : UserControl, IDisposable
    {
        /// <summary>Raised when a result was run, so the host can close and navigate.</summary>
        public event Action<string>? Executed;

        /// <summary>Raised on Escape.</summary>
        public event Action? Dismissed;

        public CommandPaletteView()
        {
            InitializeComponent();
            DataContext = ActivatorUtilities.CreateInstance<CommandPaletteViewModel>(App.Services);
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible) return;

            // Reset on each open: reopening the panel to a stale query from last time is
            // never what you want.
            if (DataContext is CommandPaletteViewModel vm) vm.SearchQuery = string.Empty;

            // After layout, or the box is not yet focusable.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>
        /// Arrows move through results while the caret stays in the box, so you can keep
        /// typing to refine without reaching for the mouse.
        /// </summary>
        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not CommandPaletteViewModel vm) return;

            switch (e.Key)
            {
                case Key.Down:
                    Step(vm, 1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    Step(vm, -1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Run(vm);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Dismissed?.Invoke();
                    e.Handled = true;
                    break;
            }
        }

        private void Step(CommandPaletteViewModel vm, int delta)
        {
            if (vm.Results.Count == 0) return;
            int index = vm.SelectedItem is null ? -1 : vm.Results.IndexOf(vm.SelectedItem);
            index = Math.Clamp(index + delta, 0, vm.Results.Count - 1);
            vm.SelectedItem = vm.Results[index];
            ResultsList.ScrollIntoView(vm.SelectedItem);
        }

        private void Run(CommandPaletteViewModel vm)
        {
            var selected = vm.SelectedItem;
            if (selected is null) return;
            vm.ExecuteSelected();
            Executed?.Invoke(selected.PageTag);
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is CommandPaletteViewModel vm) Run(vm);
        }

        public void Dispose()
        {
            IsVisibleChanged -= OnIsVisibleChanged;
            DataContext = null;
            Executed = null;
            Dismissed = null;
        }
    }
}
