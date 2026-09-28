using Arsenal.AutoUpdate;
using Arsenal.Helpers;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Arsenal.UI.Views.Windows
{
    public partial class MainWindow
    {
        private const int MaxCachedPages = 3;
        private readonly Dictionary<string, UIElement> _pageCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _pageCacheOrder = new();
        private bool _pageContentReleased;

        public void NavigateToTag(string tag)
        {
            tag = NormalizePageTag(tag);

            // A page change retires the subpage forward was offering to return to. It
            // belonged to the page being left, and that page closes its own on the way
            // out, so there would be nothing loaded to go back into.
            _subpageBackedOutOf = null;
            _pageContentReleased = false;
            RecordHistory(tag);

            // A NavigationView press can arrive through preview input, selection and
            // click events. Once this page is live, the later notifications must be
            // no-ops instead of rebuilding the same visual tree two more times.
            if (PageContentHost.Children.Count == 1 &&
                string.Equals(_viewModel.ActivePageTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                MarkActiveNavigationItem(tag);
                return;
            }

            // Creating a XAML page is the expensive part. Keep pages detached but warm
            // while the full window is open, so returning to a page is an allocation-free
            // swap. A tray return drops inactive pages immediately, then the active page
            // after the application has remained hidden for the quiet period.
            UIElement page = GetOrCreatePage(tag);

            // Leaving a page leaves whatever was drilled into on it. Closed here rather
            // than after the swap so the page that lent the header its title and
            // description gets them back while it is still the one on screen.
            _leavingPage = true;
            try { Controls.SettingsGroup.Close(); }
            finally { _leavingPage = false; }

            // Construct the replacement before removing the current page. WPF cannot
            // render between these two operations, so the user never sees an empty host.
            PageContentHost.Children.Clear();
            PageContentHost.Children.Add(page);
            AnimatePageIn(page);
            _viewModel.ActivePageTag = tag;
            MarkActiveNavigationItem(tag);
        }

        /// <summary>What the sidebar calls a destination, for the path in the bar.</summary>
        private string PageDisplayName(string tag)
        {
            string wanted = NormalizePageTag(tag);
            foreach (object? item in RootNavigationView.MenuItems.Cast<object?>()
                         .Concat(RootNavigationView.FooterMenuItems.Cast<object?>()))
            {
                if (item is NavigationViewItem entry
                    && string.Equals(NormalizePageTag(entry.TargetPageTag), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Content?.ToString() ?? wanted;
                }
            }
            return wanted;
        }

        /// <summary>How far a destination travels on its way in.</summary>
        /// <remarks>
        /// Always the same distance and always the same direction. It used to be signed
        /// by which way the selection moved down the sidebar, which meant the same act -
        /// choosing a page - looked like two different gestures depending on where the
        /// last one happened to be, and going back up the list read as the page being
        /// pushed away. One direction makes arriving somewhere look like arriving
        /// somewhere.
        /// </remarks>
        private const double PageTravel = 26;

        /// <summary>How long a destination takes to arrive.</summary>
        /// <remarks>
        /// A whole page is a lot of surface to move, and it read as a flinch at the
        /// 220ms a small control uses. The distance is unchanged: the same travel over
        /// longer reads as deliberate rather than as further.
        /// </remarks>
        private static readonly Duration PageArrival = new(TimeSpan.FromMilliseconds(320));

        /// <summary>
        /// Slides and fades a destination in.
        /// </summary>
        /// <remarks>
        /// Done here rather than with NavigationView's own Transition property, which
        /// looks like the obvious answer and does nothing: that animates the frame the
        /// control navigates itself, and this app never uses it. Destinations are built
        /// here and swapped into <c>PageContentHost</c>, so the control has nothing to
        /// animate and the setting is inert.
        ///
        /// <para>Render-only, so it costs no layout and cannot disturb a page that is
        /// measuring itself as it arrives. Nothing is held on completion: pages are
        /// cached and shown again, and a held value would outrank anything that later
        /// set opacity on one.</para>
        ///
        /// <para>The clock starts once the page has been drawn, not when it is added.
        /// The frame that first shows a page also measures it, and Drivers takes over
        /// 300ms to measure when it comes back; started at the swap, its 320ms arrival
        /// was spent before the first frame and the page simply appeared. That is what
        /// made back and forward look as though they had no animation.</para>
        /// </remarks>
        private void AnimatePageIn(UIElement page)
        {
            // A page still arriving when another is chosen is put straight into place,
            // so it is not cached half faded.
            if (_arrivingPage is { } previous && !ReferenceEquals(previous, page))
            {
                previous.ClearValue(UIElement.OpacityProperty);
                previous.RenderTransform = Transform.Identity;
            }
            _arrivingPage = page;

            var shift = new TranslateTransform();
            page.RenderTransform = shift;

            // Cubic rather than quartic. A quartic spends almost all its travel in the
            // first third and then crawls, which is what made a longer duration feel
            // slow without feeling smooth; a cubic distributes the movement more evenly
            // across the same time.
            //
            // Y, not X, and always upwards: a page rises into place the way a list of
            // results settles. Sideways is reserved for drilling into a subpage, which
            // is a different kind of movement.
            _pageArrival.Start(0, 1, PageArrival.TimeSpan.TotalMilliseconds, Controls.FrameEase.CubicOut,
                progress =>
                {
                    page.Opacity = progress;
                    shift.Y = PageTravel * (1 - progress);
                },
                () =>
                {
                    page.ClearValue(UIElement.OpacityProperty);
                    page.RenderTransform = Transform.Identity;
                    _arrivingPage = null;
                },
                afterFirstFrame: true);
        }

        private readonly Controls.FrameEase _pageArrival = new();
        private UIElement? _arrivingPage;

        private UIElement GetOrCreatePage(string tag)
        {
            if (_pageCache.TryGetValue(tag, out UIElement? page))
            {
                _pageCacheOrder.Remove(tag);
                _pageCacheOrder.AddFirst(tag);
                return page;
            }

            page = tag switch
            {
                "Home" => new HomePage(App.Services.GetRequiredService<HomeViewModel>()),
                "Performance" => new PerformancePage(App.Services.GetRequiredService<PerformanceViewModel>()),
                "Display" => new DisplayPage(App.Services.GetRequiredService<DisplayViewModel>()),
                "Battery" => new BatteryPage(App.Services.GetRequiredService<BatteryViewModel>()),
                "Lighting" => new LightingPage(App.Services.GetRequiredService<LightingViewModel>()),
                "Peripherals" => new PeripheralsPage(App.Services.GetRequiredService<PeripheralsViewModel>()),
                "Automation" => new AutomationPage(App.Services.GetRequiredService<AutomationViewModel>()),
                "Drivers" or "Updates" => new UpdatesPage(App.Services.GetRequiredService<UpdatesViewModel>()),
                "MobileCompanion" => new MobileCompanionPage(App.Services.GetRequiredService<MobileCompanionViewModel>()),
                "Advanced" => new AdvancedPage(App.Services.GetRequiredService<AdvancedViewModel>()),
                "Settings" => new SettingsPage(App.Services.GetRequiredService<SettingsViewModel>()),
                "About" => new AboutPage(App.Services.GetRequiredService<AboutViewModel>()),
                _ => new HomePage(App.Services.GetRequiredService<HomeViewModel>())
            };

            UIElement? currentOnScreen = PageContentHost.Children.Count > 0 ? PageContentHost.Children[0] : null;
            while (_pageCache.Count >= MaxCachedPages && _pageCacheOrder.Count > 0)
            {
                LinkedListNode<string>? node = _pageCacheOrder.Last;
                while (node is not null && _pageCache.TryGetValue(node.Value, out UIElement? candidate)
                    && ReferenceEquals(candidate, currentOnScreen))
                {
                    node = node.Previous;
                }

                if (node is null) break;

                string evictTag = node.Value;
                _pageCacheOrder.Remove(node);
                if (_pageCache.Remove(evictTag, out UIElement? evicted))
                {
                    EvictPage(evicted);
                }
            }

            _pageCache[tag] = page;
            _pageCacheOrder.AddFirst(tag);
            return page;
        }

        private void EvictPage(UIElement page)
        {
            if (_subpageBackButtons.Remove(page, out System.Windows.Controls.Button? button))
            {
                button.Click -= SubpageBackButton_Click;
            }
            _pageTitles.Remove(page);
            _pageSubtitles.Remove(page);
            DisposeSubtree(page);
            GC.Collect(1, GCCollectionMode.Optimized);
        }

        private static void DisposeSubtree(UIElement element)
        {
            if (element is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }

            if (element is DependencyObject parent)
            {
                foreach (DependencyObject child in Descendants(parent))
                {
                    if (child is IDisposable childDisposable)
                    {
                        try { childDisposable.Dispose(); } catch { }
                    }
                }
            }
        }

        private static string NormalizePageTag(string? tag) => tag switch
        {
            "Home" or "Performance" or "Display" or "Battery" or "Lighting" or
            "Peripherals" or "Automation" or "Drivers" or "MobileCompanion" or
            "Advanced" or "Settings" or "About" => tag,
            // Existing remote companions and command links used the old page tag.
            "Updates" => "Drivers",
            "Devices" => "Peripherals",
            _ => "Home"
        };

        /// <summary>
        /// Drops the live page while the window is hidden to the tray, and puts the same
        /// page back when it returns. Removing it from the tree raises Unloaded on the
        /// way out and Loaded on the way back, so pages that start and stop work around
        /// their own visibility keep doing so unchanged.
        /// </summary>
        private void ReleasePageContent()
        {
            if (PageContentHost.Children.Count > 0)
                PageContentHost.Children.Clear();

            // Retain the active page in the cache so reopening the window or toggling
            // the tray repeatedly reuses the existing visual tree rather than allocating
            // and rebuilding it on every open. Inactive pages visited during the session
            // are evicted and disposed immediately.
            string activeTag = NormalizePageTag(_viewModel.ActivePageTag);
            var inactive = _pageCache.Keys
                .Where(k => !string.Equals(k, activeTag, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (string tag in inactive)
            {
                if (_pageCache.Remove(tag, out UIElement? evicted))
                {
                    _pageCacheOrder.Remove(tag);
                    EvictPage(evicted);
                }
            }

            _pageContentReleased = true;

            GC.Collect(1, GCCollectionMode.Optimized);
            Services.BackgroundMemoryRelease.Schedule();
        }

        /// <summary>
        /// Releases the last warm page after the complete application has remained
        /// hidden for the background quiet period.
        /// </summary>
        internal void ReleaseHiddenPageContent()
        {
            if (IsVisible) return;

            if (PageContentHost.Children.Count > 0)
                PageContentHost.Children.Clear();

            foreach (UIElement page in _pageCache.Values.ToList())
                EvictPage(page);

            _pageCache.Clear();
            _pageCacheOrder.Clear();
            _pageContentReleased = true;
        }

        /// <summary>
        /// Whether the hidden window can be discarded without interrupting a modal or
        /// long-running operation that still owns visible state.
        /// </summary>
        internal bool CanReleaseHiddenWindow =>
            !IsVisible &&
            !_viewModel.IsCommandPaletteOpen &&
            !_viewModel.IsSetupOpen &&
            !_viewModel.IsUpdateOpen &&
            !_viewModel.IsGpuSwitching &&
            !_viewModel.IsAsusServicesChanging;

        internal void ReleaseOwnedContent()
        {
            ReleaseHiddenPageContent();
            ReleaseSearchPanel();
            ReleaseSetupPanel();
            ReleaseUpdatePanel();
        }

        private void RestorePageContent()
        {
            if (!_pageContentReleased) return;
            NavigateToTag(string.IsNullOrEmpty(_viewModel.ActivePageTag) ? "Home" : _viewModel.ActivePageTag);
        }
    }
}
