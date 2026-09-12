using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using UserControl = System.Windows.Controls.UserControl;

namespace Arsenal.UI.Views.Overlays
{
    /// <summary>
    /// The application update card. The host decides when it is on screen; this control
    /// only owns the content and forwards the one thing the host has to react to, which
    /// is the card asking to be closed.
    /// </summary>
    public partial class UpdateView : UserControl, IDisposable
    {
        private readonly UpdateOverlayViewModel _viewModel;

        /// <summary>Raised by Later, and by dismissing a failure.</summary>
        public event Action? Dismissed;

        public UpdateView()
            : this(ActivatorUtilities.CreateInstance<UpdateOverlayViewModel>(App.Services))
        {
        }

        /// <summary>Used by the smoke harness, which has no service provider.</summary>
        public UpdateView(UpdateOverlayViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _viewModel.Dismissed += OnDismissed;
            DataContext = _viewModel;
        }

        public void Present(ReleaseUpdate release) => _viewModel.Present(release);

        public void Present(UpdateInfo info) => _viewModel.Present(info);

        /// <summary>True while a package is being fetched, so the host can refuse to close.</summary>
        public bool IsBusy => _viewModel.IsDownloading;

        private void OnDismissed() => Dismissed?.Invoke();

        public void Dispose()
        {
            _viewModel.Dismissed -= OnDismissed;
            _viewModel.CancelCommand.Execute(null);
        }
    }
}
