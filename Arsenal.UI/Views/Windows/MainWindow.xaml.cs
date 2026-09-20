using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.Helpers;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Overlays;
using Arsenal.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using InputModifierKeys = System.Windows.Input.ModifierKeys;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Arsenal.UI.Views.Windows
{
    public partial class MainWindow : FluentWindow
    {
        private readonly MainViewModel _viewModel;
        private readonly DispatcherTimer _placementSaveTimer;
        private readonly Dictionary<string, UIElement> _pageCache = new(StringComparer.OrdinalIgnoreCase);
        private CommandPaletteView? _searchPanel;
        private SetupView? _setupPanel;
        private UpdateView? _updatePanel;

        private bool _allowClose;
        private bool _pageContentReleased;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            // The theme is applied before any window exists, so this window has to take
            // the transparency setting for itself rather than wait for a refresh.
            App.ApplyWindowBackdrop(this);

            _placementSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _placementSaveTimer.Tick += (_, _) =>
            {
                _placementSaveTimer.Stop();
                SaveWindowPlacementNow();
            };

            Loaded += (_, _) => { RestoreWindowPlacement(); TrackNavigationPaneWidth(); ApplyResponsivePane(); };
            LocationChanged += (_, _) => ScheduleWindowPlacementSave();
            SizeChanged += (_, e) =>
            {
                ScheduleWindowPlacementSave();
                if (e.WidthChanged) ApplyResponsivePane();
            };
            StateChanged += (_, _) => ScheduleWindowPlacementSave();

            // Navigate to Home by default
            NavigateToTag("Home");

            // This is a tray application: the window spends most of its life hidden,
            // and a hidden window still holds every element, brush and cached render
            // resource from visited pages. Keep the visible-session cache for instant
            // navigation, then drop it when the full window returns to the tray.
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue) RestorePageContent();
                else ReleasePageContent();
            };

            // Keybindings (Ctrl+K for Command Palette)
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
                {
                    ShowCommandPalette();
                    e.Handled = true;
                }
            };

            // The custom title bar can consume button/key input before bubbling reaches
            // the window. Listen to handled preview events as a reliable fallback.
            AddHandler(Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((s, e) =>
            {
                if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    ShowCommandPalette();
                    e.Handled = true;
                }
            }), true);
            AddHandler(Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(OnPreviewKeyDownForOverlays), true);

            SearchOverlay.Closed += ReleaseSearchPanel;
            SetupOverlay.Closed += ReleaseSetupPanel;
            UpdateOverlay.Closed += ReleaseUpdatePanel;

            // Coming back to the window should not look like the user had been tabbing
            // through it. Deferred to Input priority because focus restoration is still in
            // flight while Activated is being raised, and reading the focused element any
            // earlier names the element that is about to lose it.
            Activated += (_, _) => Dispatcher.BeginInvoke(
                new Action(() => _focusRing.SuppressForActivation(Keyboard.FocusedElement)),
                DispatcherPriority.Input);
            PreviewKeyDown += RestoreFocusRingOnNavigationKey;

            // The bar is the only thing that names the open subpage now, so it has to
            // hear about one opening from anywhere on any page. Dropped again on close
            // because the event is static and would otherwise hold this window for the
            // life of the process.
            Controls.SettingsGroup.OpenGroupChanged += OnOpenGroupChanged;
            Closed += (_, _) => Controls.SettingsGroup.OpenGroupChanged -= OnOpenGroupChanged;
        }

        private readonly Controls.FocusRingSuppressor _focusRing = new();

        private void RestoreFocusRingOnNavigationKey(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Controls.FocusRingSuppressor.IsNavigationKey(key)) _focusRing.RestoreForNavigation();
        }

        private void NavigationView_SelectionChanged(NavigationView sender, RoutedEventArgs args)
        {
            if (sender.SelectedItem is NavigationViewItem selectedItem)
            {
                string tag = selectedItem.TargetPageTag ?? "Home";
                NavigateToTag(tag);
            }
        }

        private void NavigationItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is NavigationViewItem item)
                NavigateToTag(item.TargetPageTag ?? "Home");
        }

        private void RootNavigationView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = FindNavigationItem(e.OriginalSource as DependencyObject);
            if (item != null) NavigateToTag(item.TargetPageTag ?? "Home");
        }

        private void RootNavigationView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            var item = FindNavigationItem(Keyboard.FocusedElement as DependencyObject);
            if (item == null) return;
            NavigateToTag(item.TargetPageTag ?? "Home");
            e.Handled = true;
        }

        private static NavigationViewItem? FindNavigationItem(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is NavigationViewItem item) return item;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        private FrameworkElement? _navigationPane;
        private FrameworkElement? _navigationContent;

        /// <summary>
        /// Finds the pane parts and keeps the window grounds tied to them.
        /// </summary>
        /// <remarks>
        /// The pane's width animates when it opens and closes - 220, 205, 175, 137, 109,
        /// 73, 46 over the transition - so a width derived from IsPaneOpen would snap the
        /// divider to its destination while the pane was still travelling. It follows the
        /// live layout instead.
        ///
        /// Width alone is not the seam, though. PaneGrid carries a 4px left margin, so at
        /// 220 wide its right edge is at 224 and a divider placed at 220 draws *inside*
        /// the pane, through the navigation items. The content presenter then starts at
        /// 229, leaving a 5px gap that would show as a stripe of bare window between the
        /// two grounds once transparency is off. So the seam is taken from where the
        /// content actually begins, and the sidebar ground runs all the way to it.
        /// </remarks>
        private void TrackNavigationPaneWidth()
        {
            _navigationPane = FindDescendant<FrameworkElement>(RootNavigationView, "PaneGrid");
            _navigationContent = FindDescendant<FrameworkElement>(
                RootNavigationView, "PART_NavigationViewContentPresenter");

            if (_navigationPane is null)
            {
                Logger.WriteLine("Navigation pane part 'PaneGrid' not found; sidebar ground will not track the pane.");
                return;
            }

            // Fires on every frame of the open/close animation, because the pane is being
            // resized by it.
            _navigationPane.SizeChanged += (_, _) => UpdateWindowSurfaces();
            UpdateWindowSurfaces();
        }

        /// <summary>Height of the title bar strip, matching the window's first row.</summary>
        private const double TitleBarHeight = 48d;

        /// <summary>How far the content ground's top-left corner rounds once tucked under
        /// the title bar.</summary>
        private const double ContentCornerRadius = 8d;

        /// <summary>
        /// Lays the content ground out against the pane, and tucks it under the title bar
        /// as the pane collapses.
        /// </summary>
        /// <remarks>
        /// Fully open, the content ground is a plain rectangle filling the right of the
        /// window from the very top. As the pane closes it slides down below the title bar
        /// and rounds its top-left corner, which leaves the navigation ground running
        /// across the whole strip - so the toggle, history and search icons sit on the
        /// navigation colour instead of hanging over the content.
        ///
        /// The morph is driven from the pane's own animated width rather than from a
        /// separate clock, so it is exactly as fluid as the pane and can never fall out of
        /// step with it. The pane bottoms out 8px under CompactPaneLength because its grid
        /// carries a 4px margin on each side; if that template detail ever changes the
        /// morph simply finishes slightly early or late.
        /// </remarks>
        private void UpdateWindowSurfaces()
        {
            if (_navigationPane is null || !_navigationPane.IsVisible) return;

            try
            {
                double seam = _navigationContent is { IsVisible: true }
                    ? _navigationContent.TransformToAncestor(RootNavigationView).Transform(default).X
                    : _navigationPane.TransformToAncestor(RootNavigationView)
                        .Transform(new System.Windows.Point(_navigationPane.ActualWidth, 0)).X;

                if (seam <= 0 || double.IsNaN(seam) || double.IsInfinity(seam)) return;

                double open = RootNavigationView.OpenPaneLength;
                double compact = Math.Max(0, RootNavigationView.CompactPaneLength - 8);
                double travel = Math.Max(1, open - compact);
                double collapsed = 1 - Math.Clamp((_navigationPane.ActualWidth - compact) / travel, 0, 1);

                var margin = new Thickness(seam, collapsed * TitleBarHeight, 0, 0);
                var corner = new CornerRadius(collapsed * ContentCornerRadius, 0, 0, 0);

                // The left divider is always there. The top one is the underside of the
                // title bar, which only exists once the content has tucked beneath it, so
                // it arrives with the collapse and carries on round the corner into the
                // left edge. Fully open there is no title bar to underline and its
                // thickness is zero.
                var border = new Thickness(1, collapsed, 0, 0);

                // This runs inside a layout pass, so writing values back unchanged would
                // invalidate layout again for nothing.
                if (Math.Abs(ContentSurface.Margin.Left - margin.Left) < 0.5 &&
                    Math.Abs(ContentSurface.Margin.Top - margin.Top) < 0.5)
                    return;

                ContentSurface.Margin = margin;
                ContentSurface.CornerRadius = corner;
                ContentSurface.BorderThickness = border;
            }
            catch (InvalidOperationException)
            {
                // The pane is not connected to the same visual root yet; the next resize
                // brings us back here with a usable tree.
            }
        }

        private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed && typed.Name == name) return typed;
                if (FindDescendant<T>(child, name) is { } found) return found;
            }
            return null;
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e) => ShowCommandPalette();

        private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
        {
            RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;

            // The last thing the user asked for by hand is what the pane goes back to
            // when the window is widened again.
            _responsivePane.UserSetPaneOpen(RootNavigationView.IsPaneOpen);
        }

        private readonly Controls.ResponsivePaneState _responsivePane = new();

        /// <summary>
        /// Folds the navigation column away as the window approaches its minimum width,
        /// and brings it back when there is room again.
        /// </summary>
        /// <remarks>
        /// Nothing is animated here. <c>IsPaneOpen</c> is what the NavigationView
        /// animates on its own, and the window grounds already follow the pane's live
        /// width frame by frame through <see cref="UpdateWindowSurfaces"/> - so setting
        /// the flag produces exactly the motion the toggle button produces, which is
        /// what makes an automatic collapse indistinguishable from a manual one.
        /// </remarks>
        private void ApplyResponsivePane()
        {
            if (!IsLoaded) return;

            bool? open = _responsivePane.Evaluate(ActualWidth, MinWidth, RootNavigationView.IsPaneOpen);
            if (open is not null) RootNavigationView.IsPaneOpen = open.Value;
        }

        /// <summary>
        /// Pages are swapped into the content overlay by hand rather than driven through
        /// a frame, so the pane's own selection tracking never engages and nothing shows
        /// where you are. Marking the matching item active drives the accent indicator.
        /// </summary>
        private void MarkActiveNavigationItem(string tag)
        {
            foreach (NavigationViewItem item in RootNavigationView.MenuItems.OfType<NavigationViewItem>()
                        .Concat(RootNavigationView.FooterMenuItems.OfType<NavigationViewItem>()))
            {
                item.IsActive = string.Equals(item.TargetPageTag, tag, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Pages visited in order, with <see cref="_historyIndex"/> pointing at the one on
        /// screen. Going back moves the index rather than dropping entries, so forward
        /// stays available until a new destination is chosen.
        /// </summary>
        private readonly List<string> _history = new();
        private int _historyIndex = -1;

        /// <summary>Set while replaying history, so the trip is not recorded as a new one.</summary>
        private bool _navigatingHistory;

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
            if (Controls.SettingsGroup.OpenGroup is not null)
            {
                Controls.SettingsGroup.Close();
                return;
            }

            if (_historyIndex <= 0) return;
            _historyIndex--;
            ReplayHistory();
        }

        private void GoForward()
        {
            if (_historyIndex < 0 || _historyIndex >= _history.Count - 1) return;
            _historyIndex++;
            ReplayHistory();
        }

        private void ReplayHistory()
        {
            _navigatingHistory = true;
            try { NavigateToTag(_history[_historyIndex]); }
            finally { _navigatingHistory = false; }
            UpdateHistoryButtons();
        }

        private void RecordHistory(string tag)
        {
            if (_navigatingHistory) return;

            // Re-selecting the page already on screen is not a journey.
            if (_historyIndex >= 0 &&
                string.Equals(_history[_historyIndex], tag, StringComparison.OrdinalIgnoreCase))
                return;

            // A new destination abandons whatever was ahead of here.
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

            _history.Add(tag);
            _historyIndex = _history.Count - 1;
            UpdateHistoryButtons();
        }

        private void UpdateHistoryButtons()
        {
            // An open subpage is somewhere to go back from even on the first page of
            // the session, which is exactly the case where the history says otherwise.
            NavigateBackButton.IsEnabled = _historyIndex > 0 || Controls.SettingsGroup.OpenGroup is not null;
            NavigateForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        }

        public void NavigateToTag(string tag)
        {
            tag = NormalizePageTag(tag);
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
            // swap. ReleasePageContent clears this cache when the window goes to the tray.
            UIElement page = GetOrCreatePage(tag);

            // Construct the replacement before removing the current page. WPF cannot
            // render between these two operations, so the user never sees an empty host.
            // Which way the sidebar moved, so the page arrives from the direction the
            // selection travelled. Taken before ActivePageTag is overwritten.
            double from = ArrivalOffset(_viewModel.ActivePageTag, tag);

            PageContentHost.Children.Clear();
            PageContentHost.Children.Add(page);
            AnimatePageIn(page, from);
            _viewModel.ActivePageTag = tag;
            MarkActiveNavigationItem(tag);

            // A page change closes whatever was drilled into, so the path is just the
            // page again. Set after the swap, since the close that clears the subpage
            // segment may have already run.
            BreadcrumbRoot.Text = PageDisplayName(tag);
            ShowSubpageCrumb(Controls.SettingsGroup.OpenGroup?.Header);
        }

        /// <summary>
        /// How far, and from which side, a destination starts.
        /// </summary>
        /// <remarks>
        /// Vertical, because the list it is chosen from is vertical: picking something
        /// further down the sidebar and watching the page rise to meet it says which
        /// way you moved. Sideways is kept for drilling into a subpage, where the
        /// movement is into the page rather than along the list, so the two reads never
        /// mean the same thing.
        /// </remarks>
        private double ArrivalOffset(string? fromTag, string toTag)
        {
            int from = PageOrdinal(fromTag);
            int to = PageOrdinal(toTag);
            if (from < 0 || to < 0 || from == to) return PageTravel;
            return to > from ? PageTravel : -PageTravel;
        }

        /// <summary>Where a destination sits in the sidebar, or -1 if it is not in it.</summary>
        /// <remarks>
        /// Read from the live collections rather than from a list written out here, so
        /// reordering the sidebar cannot silently reverse a transition.
        /// </remarks>
        private int PageOrdinal(string? tag)
        {
            if (string.IsNullOrEmpty(tag)) return -1;
            string wanted = NormalizePageTag(tag);
            int index = 0;

            foreach (object? item in RootNavigationView.MenuItems.Cast<object?>()
                         .Concat(RootNavigationView.FooterMenuItems.Cast<object?>()))
            {
                if (item is NavigationViewItem entry)
                {
                    if (string.Equals(NormalizePageTag(entry.TargetPageTag), wanted, StringComparison.OrdinalIgnoreCase))
                        return index;
                    index++;
                }
            }
            return -1;
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
        private const double PageTravel = 26;

        // ---- The path in the title bar ------------------------------------------

        private static readonly Duration CrumbIn = new(TimeSpan.FromMilliseconds(260));
        private static readonly Duration CrumbOut = new(TimeSpan.FromMilliseconds(170));

        /// <summary>
        /// Each page's own description line, and the words it started with.
        /// </summary>
        /// <remarks>
        /// Found by walking the page once rather than by every page exposing it,
        /// because the alternative was editing a dozen page headers to say something
        /// the shared style already says. Keyed on the page instance, which is cached
        /// and reused, so the original survives any number of trips in and out of a
        /// subpage.
        /// </remarks>
        private readonly Dictionary<UIElement, (System.Windows.Controls.TextBlock Line, string Original)> _pageSubtitles = new();

        private void OnOpenGroupChanged(Controls.SettingsGroup? group)
        {
            // Raised from whichever page the group is on, which is this thread, but a
            // group closing during teardown can arrive while the bar is already gone.
            if (!IsLoaded) return;

            ShowSubpageCrumb(group?.Header);
            ShowSubpageDescription(group?.Description);
            UpdateHistoryButtons();
        }

        /// <summary>
        /// Slides the subpage segment in beside the page name, or takes it away.
        /// </summary>
        /// <remarks>
        /// Render-only and released on completion, for the same reason the page
        /// arrival is: the bar outlives every page, and an animation left holding its
        /// final value outranks whatever sets opacity on it next.
        /// </remarks>
        private void ShowSubpageCrumb(string? subpage)
        {
            bool wanted = !string.IsNullOrWhiteSpace(subpage);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            if (wanted)
            {
                BreadcrumbLeaf.Text = subpage;
                BreadcrumbTail.Visibility = Visibility.Visible;

                BreadcrumbTail.BeginAnimation(OpacityProperty, new DoubleAnimation(1, CrumbIn) { EasingFunction = ease });
                BreadcrumbTailShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, CrumbIn) { EasingFunction = ease });
                return;
            }

            if (BreadcrumbTail.Visibility != Visibility.Visible) return;

            var fade = new DoubleAnimation(0, CrumbOut) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                // Only if nothing opened again while this was running, or the segment
                // that just arrived would be hidden by the departure of the last one.
                if (Controls.SettingsGroup.OpenGroup is not null) return;
                BreadcrumbTail.BeginAnimation(OpacityProperty, null);
                BreadcrumbTail.Opacity = 0;
                BreadcrumbTail.Visibility = Visibility.Collapsed;
            };

            BreadcrumbTail.BeginAnimation(OpacityProperty, fade);
            BreadcrumbTailShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-10, CrumbOut) { EasingFunction = ease });
        }

        /// <summary>
        /// Puts the subpage's description on the page's own description line.
        /// </summary>
        /// <remarks>
        /// The subpage no longer has anywhere to say this itself, and the page's line
        /// is describing something the user has just navigated past. Cross-faded rather
        /// than swapped, because the words change length and a hard cut reads as the
        /// header twitching.
        /// </remarks>
        private void ShowSubpageDescription(string? description)
        {
            if (PageSubtitle() is not { } subtitle) return;
            string wanted = string.IsNullOrWhiteSpace(description) ? subtitle.Original : description;
            if (string.Equals(subtitle.Line.Text, wanted, StringComparison.Ordinal)) return;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var out_ = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(110))) { EasingFunction = ease };
            out_.Completed += (_, _) =>
            {
                subtitle.Line.BeginAnimation(OpacityProperty, null);
                subtitle.Line.Text = wanted;
                subtitle.Line.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = ease });
            };
            subtitle.Line.BeginAnimation(OpacityProperty, out_);
        }

        private (System.Windows.Controls.TextBlock Line, string Original)? PageSubtitle()
        {
            if (PageContentHost.Children.Count != 1) return null;
            UIElement page = PageContentHost.Children[0];
            if (_pageSubtitles.TryGetValue(page, out var known)) return known;

            var style = TryFindResource("PageSubtitleStyle") as Style;
            if (style is null) return null;

            System.Windows.Controls.TextBlock? found = Descendants(page)
                .OfType<System.Windows.Controls.TextBlock>()
                .FirstOrDefault(text => ReferenceEquals(text.Style, style));
            if (found is null) return null;

            var entry = (found, found.Text);
            _pageSubtitles[page] = entry;
            return entry;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
            }
        }

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
        /// measuring itself as it arrives. The animations are released on completion
        /// rather than left holding their final value: pages are cached and shown again,
        /// and a held animation would outrank anything that later set opacity on one.</para>
        /// </remarks>
        private static void AnimatePageIn(UIElement page, double fromY)
        {
            var shift = new TranslateTransform();
            page.RenderTransform = shift;

            // Cubic rather than quartic. A quartic spends almost all its travel in the
            // first third and then crawls, which is what made a longer duration feel
            // slow without feeling smooth; a cubic distributes the movement more evenly
            // across the same time.
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var slide = new DoubleAnimation(fromY, 0, PageArrival) { EasingFunction = ease };
            var fade = new DoubleAnimation(0, 1, PageArrival) { EasingFunction = ease };

            fade.Completed += (_, _) =>
            {
                page.BeginAnimation(UIElement.OpacityProperty, null);
                page.Opacity = 1;
                page.RenderTransform = Transform.Identity;
            };

            // Y, not X. The sidebar is a column, so a page arriving from the direction
            // the selection moved says which way you went; sideways is reserved for
            // drilling into a subpage, which is a different kind of movement.
            shift.BeginAnimation(TranslateTransform.YProperty, slide);
            page.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private UIElement GetOrCreatePage(string tag)
        {
            if (_pageCache.TryGetValue(tag, out UIElement? page)) return page;

            page = tag switch
            {
                "Home" => new HomePage(App.Services.GetRequiredService<HomeViewModel>()),
                "Performance" => new PerformancePage(App.Services.GetRequiredService<PerformanceViewModel>()),
                "Display" => new DisplayPage(App.Services.GetRequiredService<DisplayViewModel>()),
                "Battery" => new BatteryPage(App.Services.GetRequiredService<BatteryViewModel>()),
                "Lighting" => new LightingPage(App.Services.GetRequiredService<LightingViewModel>()),
                "Devices" => new DevicesPage(App.Services.GetRequiredService<DevicesViewModel>()),
                "Automation" => new AutomationPage(App.Services.GetRequiredService<AutomationViewModel>()),
                "Drivers" or "Updates" => new UpdatesPage(App.Services.GetRequiredService<UpdatesViewModel>()),
                "MobileCompanion" => new MobileCompanionPage(App.Services.GetRequiredService<MobileCompanionViewModel>()),
                "Advanced" => new AdvancedPage(App.Services.GetRequiredService<AdvancedViewModel>()),
                "Settings" => new SettingsPage(App.Services.GetRequiredService<SettingsViewModel>()),
                "About" => new AboutPage(App.Services.GetRequiredService<AboutViewModel>()),
                _ => new HomePage(App.Services.GetRequiredService<HomeViewModel>())
            };

            _pageCache[tag] = page;
            return page;
        }

        private static string NormalizePageTag(string? tag) => tag switch
        {
            "Home" or "Performance" or "Display" or "Battery" or "Lighting" or
            "Devices" or "Automation" or "Drivers" or "MobileCompanion" or
            "Advanced" or "Settings" or "About" => tag,
            // Existing remote companions and command links used the old page tag.
            "Updates" => "Drivers",
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

            // Preserve the tray-memory optimization: cached page controls live only for
            // the visible full-window session and become collectible as soon as it hides.
            _pageCache.Clear();
            _pageContentReleased = true;

            // Dropping the references only makes the pages collectible. Without this the
            // collection happens whenever the GC next feels like it - which, for a tray
            // application that then sits idle allocating almost nothing, can be never -
            // so the window's whole visual tree stayed resident for the rest of the
            // session.
            Services.BackgroundMemoryRelease.Schedule();
        }

        private void RestorePageContent()
        {
            if (!_pageContentReleased) return;
            NavigateToTag(string.IsNullOrEmpty(_viewModel.ActivePageTag) ? "Home" : _viewModel.ActivePageTag);
        }

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

        /// <summary>
        /// Escape closes whichever panel is open. Handled on the window rather than in
        /// each panel so it works no matter where focus currently sits inside it.
        ///
        /// Panels that opt out of light dismiss - setup, which would lose the steps
        /// answered so far - are left alone; they close through their own buttons.
        /// </summary>
        private void OnPreviewKeyDownForOverlays(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;

            if (_viewModel.IsCommandPaletteOpen) _viewModel.IsCommandPaletteOpen = false;
            else if (_viewModel.IsSetupOpen && SetupOverlay.IsLightDismissEnabled) _viewModel.IsSetupOpen = false;
            else return;

            e.Handled = true;
        }

        /// <summary>
        /// Re-applies the backdrop once the window has a handle.
        /// </summary>
        /// <remarks>
        /// The composition target does not exist in the constructor, and its clear colour
        /// is what a resize uncovers along the edge being dragged. This is the first
        /// moment it can be set.
        /// </remarks>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            App.ApplyWindowBackdrop(this);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _placementSaveTimer.Stop();
            SaveWindowPlacementNow();
            if (!_allowClose && AppConfig.IsNotFalse("minimize_to_tray"))
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                base.OnClosing(e);
                if (!_allowClose)
                    Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown()));
            }
        }

        public void AllowClose() => _allowClose = true;

        private void RestoreWindowPlacement()
        {
            Width = Math.Max(MinWidth, AppConfig.Get("window_width", 1180));
            Height = Math.Max(MinHeight, AppConfig.Get("window_height", 780));
            int left = AppConfig.Get("window_left", int.MinValue);
            int top = AppConfig.Get("window_top", int.MinValue);
            if (left != int.MinValue && top != int.MinValue)
            {
                Left = left;
                Top = top;
            }
            if (AppConfig.Is("window_maximized")) WindowState = WindowState.Maximized;
        }

        private void ScheduleWindowPlacementSave()
        {
            if (!IsLoaded) return;
            _placementSaveTimer.Stop();
            _placementSaveTimer.Start();
        }

        private void SaveWindowPlacementNow()
        {
            if (!IsLoaded) return;
            SetConfigIfChanged("window_maximized", WindowState == WindowState.Maximized ? 1 : 0);
            if (WindowState != WindowState.Normal) return;
            SetConfigIfChanged("window_width", (int)ActualWidth);
            SetConfigIfChanged("window_height", (int)ActualHeight);
            SetConfigIfChanged("window_left", (int)Left);
            SetConfigIfChanged("window_top", (int)Top);
        }

        private static void SetConfigIfChanged(string key, int value)
        {
            if (AppConfig.Get(key, int.MinValue) != value) AppConfig.Set(key, value);
        }

    }
}
