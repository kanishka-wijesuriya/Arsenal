using Arsenal.AutoUpdate;
using Arsenal.Helpers;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Overlays;
using Arsenal.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using InputModifierKeys = System.Windows.Input.ModifierKeys;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Arsenal.UI.Views.Windows
{
    public partial class MainWindow
    {
        /// <summary>
        /// Pages visited in order, with the saved index pointing at the one on
        /// screen. Going back moves the index rather than dropping entries, so forward
        /// stays available until a new destination is chosen. The state is owned outside
        /// this window so a long tray idle can release the native window without losing
        /// the user's navigation trail.
        /// </summary>
        private List<string> History => _windowState.History;
        private int HistoryIndex
        {
            get => _windowState.HistoryIndex;
            set => _windowState.HistoryIndex = value;
        }

        /// <summary>Set while replaying history, so the trip is not recorded as a new one.</summary>
        private bool _navigatingHistory;

        /// <summary>
        /// The subpage <see cref="GoBack"/> last stepped out of, so that forward can
        /// step back into it. Null once anything else has happened.
        /// </summary>
        /// <remarks>
        /// Weak on purpose. A subpage belongs to a page, and pages are kept warm while
        /// detached and then dropped once the window has been away, so a strong
        /// reference here would hold one alive for a trip the user may never make.
        /// </remarks>
        private WeakReference<Controls.SettingsGroup>? _subpageBackedOutOf;

        private void NavigateBackButton_Click(object sender, RoutedEventArgs e) => GoBack();

        private void NavigateForwardButton_Click(object sender, RoutedEventArgs e) => GoForward();

        /// <summary>
        /// Leaves the open subpage, or the page, in that order.
        /// </summary>
        /// <remarks>
        /// One back control for both, because there is only one place to look for it.
        /// A subpage used to carry its own, which meant the bar's arrow skipped past
        /// whatever the user was actually inside and left the page instead.
        /// </remarks>
        private void GoBack()
        {
            if (Controls.SettingsGroup.OpenGroup is { } leaving)
            {
                _subpageBackedOutOf = new WeakReference<Controls.SettingsGroup>(leaving);
                Controls.SettingsGroup.Close();
                return;
            }

            if (HistoryIndex <= 0) return;
            HistoryIndex--;
            ReplayHistory();
        }

        /// <summary>
        /// Steps back into the subpage back stepped out of, or on to the next page, in
        /// that order.
        /// </summary>
        /// <remarks>
        /// The mirror of <see cref="GoBack"/>, which was missing its first half. The
        /// history holds pages and a subpage is not one, so leaving a subpage was the
        /// single step forward could not undo: it sailed past it to the next page.
        /// </remarks>
        private void GoForward()
        {
            if (ReopenableSubpage() is { } returning)
            {
                returning.Open();
                return;
            }

            if (HistoryIndex < 0 || HistoryIndex >= History.Count - 1) return;
            HistoryIndex++;
            ReplayHistory();
        }

        /// <summary>The subpage forward would return to, or null when there is none.</summary>
        private Controls.SettingsGroup? ReopenableSubpage()
        {
            if (Controls.SettingsGroup.OpenGroup is not null) return null;
            if (_subpageBackedOutOf is null ||
                !_subpageBackedOutOf.TryGetTarget(out Controls.SettingsGroup? group)) return null;

            // Its page has to be the one on screen. A group on a page that is detached
            // but kept warm is not loaded, and drilling into that would open a subpage
            // of a page nobody is looking at.
            return group.IsLoaded ? group : null;
        }

        private void ReplayHistory()
        {
            _navigatingHistory = true;
            try { NavigateToTag(History[HistoryIndex]); }
            finally { _navigatingHistory = false; }
            UpdateHistoryButtons();
        }

        private void RecordHistory(string tag)
        {
            if (_navigatingHistory) return;

            // Re-selecting the page already on screen is not a journey.
            if (HistoryIndex >= 0 &&
                string.Equals(History[HistoryIndex], tag, StringComparison.OrdinalIgnoreCase))
                return;

            // A new destination abandons whatever was ahead of here.
            if (HistoryIndex < History.Count - 1)
                History.RemoveRange(HistoryIndex + 1, History.Count - HistoryIndex - 1);

            History.Add(tag);
            HistoryIndex = History.Count - 1;
            UpdateHistoryButtons();
        }

        private void UpdateHistoryButtons()
        {
            // An open subpage is somewhere to go back from even on the first page of
            // the session, which is exactly the case where the history says otherwise.
            NavigateBackButton.IsEnabled = HistoryIndex > 0 || Controls.SettingsGroup.OpenGroup is not null;
            NavigateForwardButton.IsEnabled = (HistoryIndex >= 0 && HistoryIndex < History.Count - 1)
                                              || ReopenableSubpage() is not null;
        }

        /// <summary>
        /// Escape closes whichever panel is open. Handled on the window rather than in
        /// each panel so it works no matter where focus currently sits inside it.
        ///
        /// Panels that opt out of light dismiss - setup, which would lose the steps
        /// answered so far - are left alone; they close through their own buttons.
        /// </summary>
        /// <summary>
        /// The back and forward buttons on a mouse, driving the same navigation the
        /// arrows in the title bar do.
        /// </summary>
        /// <remarks>
        /// Windows sends these as ordinary mouse buttons and nothing routes them to
        /// anything by default, so an application that does not look for them simply
        /// does nothing when they are pressed. This one has a history and a way back out
        /// of a subpage already; this is the same two steps under the thumb.
        ///
        /// <para>On the press rather than the release, which is where every browser acts
        /// and therefore where these buttons feel late if you wait. Marked handled so the
        /// control under the pointer does not also get to interpret it.</para>
        ///
        /// <para>Ignored while a panel is open over the page. Navigating behind a search
        /// palette or a half-finished setup wizard moves a page nobody can see and
        /// leaves the panel sitting on top of a different one.</para>
        /// </remarks>
        private void OnPreviewMouseNavigate(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
            if (_viewModel.IsCommandPaletteOpen || _viewModel.IsSetupOpen || _viewModel.IsUpdateOpen) return;

            if (e.ChangedButton == MouseButton.XButton1) GoBack();
            else GoForward();

            e.Handled = true;
        }

        /// <summary>
        /// The keyboard's ways of saying back and forward.
        /// </summary>
        /// <remarks>
        /// Alt with an arrow is the Windows convention, and the browser keys are what a
        /// keyboard with a dedicated pair sends. Both are here because the side buttons
        /// on a mouse do not always arrive as mouse buttons: plenty of vendor drivers
        /// are configured to send Alt+Left and Alt+Right instead, and a device bound
        /// that way would otherwise look like one this application ignores.
        /// </remarks>
        private void OnPreviewKeyDownForNavigation(object sender, KeyEventArgs e)
        {
            if (_viewModel.IsCommandPaletteOpen || _viewModel.IsSetupOpen || _viewModel.IsUpdateOpen) return;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool alt = Keyboard.Modifiers.HasFlag(InputModifierKeys.Alt);

            if (key == Key.BrowserBack || (alt && key == Key.Left)) GoBack();
            else if (key == Key.BrowserForward || (alt && key == Key.Right)) GoForward();
            else return;

            e.Handled = true;
        }
    }
}
