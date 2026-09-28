using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Overlays;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Arsenal.UI.Views.Windows
{
    public partial class MainWindow
    {
        private CommandPaletteView? _searchPanel;
        private SetupView? _setupPanel;
        private UpdateView? _updatePanel;

        public void ShowCommandPalette()
        {
            EnsureSearchPanel();
            _viewModel.IsCommandPaletteOpen = true;
        }

        public void ShowSetup()
        {
            // Always from the top, whether this is the first run or "Run setup again".
            EnsureSetupPanel().Restart();
            _viewModel.IsSetupOpen = true;
        }

        private void EnsureSearchPanel()
        {
            if (_searchPanel is not null) return;
            _searchPanel = new CommandPaletteView();
            _searchPanel.Dismissed += OnSearchDismissed;
            _searchPanel.Executed += OnSearchExecuted;
            SearchOverlay.Content = _searchPanel;
        }

        private SetupView EnsureSetupPanel()
        {
            if (_setupPanel is not null) return _setupPanel;
            _setupPanel = new SetupView();
            _setupPanel.Completed += OnSetupCompleted;
            SetupOverlay.Content = _setupPanel;
            return _setupPanel;
        }

        /// <summary>True while the update card is on screen.</summary>
        public bool IsUpdateCardOpen => _viewModel.IsUpdateOpen;

        /// <summary>
        /// Shows the application update card for a release the feed check found.
        /// Called from App once the signed feed reports something newer, and from the
        /// About page when the same check is run by hand.
        /// </summary>
        public void ShowApplicationUpdate(ReleaseUpdate release)
        {
            EnsureUpdatePanel().Present(release);
            _viewModel.IsUpdateOpen = true;
        }

        public void ShowApplicationUpdate(UpdateInfo info)
        {
            EnsureUpdatePanel().Present(info);
            _viewModel.IsUpdateOpen = true;
        }

        private UpdateView EnsureUpdatePanel()
        {
            if (_updatePanel is not null) return _updatePanel;
            _updatePanel = new UpdateView();
            _updatePanel.Dismissed += OnUpdateDismissed;
            UpdateOverlay.Content = _updatePanel;
            return _updatePanel;
        }

        private void OnUpdateDismissed() => _viewModel.IsUpdateOpen = false;

        private void ReleaseUpdatePanel()
        {
            if (_updatePanel is null || _viewModel.IsUpdateOpen) return;
            _updatePanel.Dismissed -= OnUpdateDismissed;
            _updatePanel.Dispose();
            UpdateOverlay.Content = null;
            _updatePanel = null;
        }

        private void OnSearchDismissed() => _viewModel.IsCommandPaletteOpen = false;

        private void OnSearchExecuted(string tag)
        {
            _viewModel.IsCommandPaletteOpen = false;
            if (!string.IsNullOrEmpty(tag)) NavigateToTag(tag);
        }

        private void OnSetupCompleted() => _viewModel.IsSetupOpen = false;

        private void ReleaseSearchPanel()
        {
            if (_searchPanel is null || _viewModel.IsCommandPaletteOpen) return;
            _searchPanel.Dismissed -= OnSearchDismissed;
            _searchPanel.Executed -= OnSearchExecuted;
            _searchPanel.Dispose();
            SearchOverlay.Content = null;
            _searchPanel = null;
        }

        private void ReleaseSetupPanel()
        {
            if (_setupPanel is null || _viewModel.IsSetupOpen) return;
            _setupPanel.Completed -= OnSetupCompleted;
            _setupPanel.Dispose();
            SetupOverlay.Content = null;
            _setupPanel = null;
        }

        private void OnPreviewKeyDownForOverlays(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;

            if (_viewModel.IsCommandPaletteOpen) _viewModel.IsCommandPaletteOpen = false;
            else if (_viewModel.IsSetupOpen && SetupOverlay.IsLightDismissEnabled) _viewModel.IsSetupOpen = false;
            else return;

            e.Handled = true;
        }
    }
}
