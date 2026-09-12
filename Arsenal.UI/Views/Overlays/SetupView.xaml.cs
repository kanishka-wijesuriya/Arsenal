using Arsenal.UI.ViewModels;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Microsoft.Extensions.DependencyInjection;

namespace Arsenal.UI.Views.Overlays
{
    public partial class SetupView : UserControl, IDisposable
    {
        /// <summary>Raised when setup finished or was skipped, so the host can close.</summary>
        public event Action? Completed;

        private readonly SetupViewModel _viewModel;

        public SetupView()
            : this(ActivatorUtilities.CreateInstance<SetupViewModel>(App.Services))
        {
        }

        /// <summary>Used by the smoke harness, which has no service provider.</summary>
        public SetupView(SetupViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;
            _viewModel.Completed += OnViewModelCompleted;
        }

        /// <summary>
        /// Puts the wizard back on its first step. The view model is a singleton, so
        /// "Run setup again" in Settings would otherwise reopen on the finish panel of
        /// the previous run.
        /// </summary>
        public void Restart() => _viewModel.Restart();

        private void OnViewModelCompleted() => Dispatcher.Invoke(() => Completed?.Invoke());

        public void Dispose()
        {
            _viewModel.Completed -= OnViewModelCompleted;
            _viewModel.Dispose();
            DataContext = null;
            Completed = null;
        }
    }
}
