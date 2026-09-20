using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// A titled card that stacks <see cref="SettingsRow"/> children and draws the
    /// hairlines between them.
    ///
    /// Dividers are recomputed whenever a child is shown or hidden, so the
    /// capability-gated rows (Mini-LED, MUX, Slash, ...) never leave a stray line
    /// behind when the hardware does not support them.
    /// </summary>
    public class SettingsGroup : ItemsControl
    {

        static SettingsGroup()
        {
            // Coercion, not a template trigger setting Visibility.
            //
            // Pages bind a group's Visibility to the hardware it needs - the OLED group,
            // the MUX group, the Mini-LED group - and a local binding beats a setter
            // from inside the control's own template. Hiding siblings that way left
            // exactly the capability-gated groups on screen inside an open subpage.
            // Coercion sits above the binding instead of fighting it, and releases it
            // again when the group is no longer dimmed.
            VisibilityProperty.OverrideMetadata(
                typeof(SettingsGroup),
                new FrameworkPropertyMetadata(
                    Visibility.Visible,
                    FrameworkPropertyMetadataOptions.AffectsMeasure
                        | FrameworkPropertyMetadataOptions.AffectsArrange,
                    null,
                    CoerceVisibility));
        }

        private static object CoerceVisibility(DependencyObject d, object baseValue)
            => ((SettingsGroup)d).IsDimmed ? Visibility.Collapsed : baseValue;

        public SettingsGroup()
        {
            ItemContainerGenerator.StatusChanged += (_, _) =>
                Dispatcher.BeginInvoke(UpdateDividers, System.Windows.Threading.DispatcherPriority.Loaded);

            // Paired with Unloaded rather than taken in the constructor: a page is
            // loaded and unloaded repeatedly as the user navigates, and a subscription
            // dropped on the first departure would leave the group deaf on return.
            // Unsubscribing first keeps a repeated Loaded from stacking handlers.
            Loaded += (_, _) =>
            {
                OpenChanged -= OnOpenChanged;
                OpenChanged += OnOpenChanged;

                // Recomputed on arrival rather than trusted from last time. A page is
                // built once and shown again on every visit, so a group carries
                // whatever state it was left in; asking the current one is the only
                // reading that is certainly right.
                OnOpenChanged(_open);
                UpdateDividers();
            };

            // Leaving a page closes whatever was drilled into on it, so coming back
            // shows the list of groups rather than wherever the user stopped. The
            // static event would otherwise hold every group a page has ever built.
            //
            // Each group clears its own state here instead of waiting to be told. The
            // groups on a page unload in an order nothing guarantees, so a sibling that
            // went first had already unsubscribed by the time the open one broadcast
            // its close - it never heard the reset, stayed dimmed, and came back
            // invisible. Leaving on foot rather than waiting for the message makes the
            // order stop mattering.
            // Checked a beat later rather than acted on at once. An unload is not
            // necessarily a departure: WPF raises Unloaded and Loaded as a pair whenever
            // a subtree is detached and put straight back, which is what happens when a
            // control inside a group shows or hides rows around it. Closing on the spot
            // therefore threw the user out of the group they were working in the moment
            // they touched such a toggle, and only such a toggle, which is what made it
            // look occasional. By the time this runs, a group that was merely re-parented
            // is loaded again and there is nothing to do.
            Unloaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsLoaded) return;

                OpenChanged -= OnOpenChanged;
                if (IsOpen) Close();
                IsOpen = false;
                IsDimmed = false;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsGroup),
                new PropertyMetadata(null));

        /// <summary>Section title rendered above the card. Hidden when empty.</summary>
        public string? Header
        {
            get => (string?)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsGroup),
                new PropertyMetadata(null));

        /// <summary>One-line explanation under the section title. Hidden when empty.</summary>
        public string? Description
        {
            get => (string?)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(SettingsGroup),
                new PropertyMetadata(SymbolRegular.Empty));

        /// <summary>Optional glyph beside the section title.</summary>
        public SymbolRegular Icon
        {
            get => (SymbolRegular)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly DependencyProperty HeaderContentProperty =
            DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(SettingsGroup),
                new PropertyMetadata(null));

        /// <summary>Optional action content aligned to the right of the section title.</summary>
        public object? HeaderContent
        {
            get => GetValue(HeaderContentProperty);
            set => SetValue(HeaderContentProperty, value);
        }

        public static readonly DependencyProperty FooterProperty =
            DependencyProperty.Register(nameof(Footer), typeof(object), typeof(SettingsGroup),
                new PropertyMetadata(null));

        /// <summary>Optional content rendered under the card, outside the row list.</summary>
        public object? Footer
        {
            get => GetValue(FooterProperty);
            set => SetValue(FooterProperty, value);
        }

        // ===== Drill-in =====
        //
        // A theme can ask for its settings to live one level down instead of all at once
        // on a long page: every group collapses to a panel you press, and pressing one
        // leaves that group alone on the page with a way back. It reads as a subpage and
        // is not one, which is the point - the alternative is a second copy of every
        // page's XAML, or moving live rows into an overlay and back out again, and rows
        // that are mid-animation or hold an open picker do not survive being reparented.
        // Here nothing moves. The open group stays where it is and its siblings go
        // Collapsed, so the page is left showing one group and a back bar.

        public static readonly DependencyProperty DrillInProperty =
            DependencyProperty.Register(nameof(DrillIn), typeof(bool), typeof(SettingsGroup),
                new PropertyMetadata(false, OnDrillInChanged));

        /// <summary>
        /// Present this group as a panel that opens, rather than as an open card. Set
        /// from the theme, not from a page.
        /// </summary>
        public bool DrillIn
        {
            get => (bool)GetValue(DrillInProperty);
            set => SetValue(DrillInProperty, value);
        }

        private static void OnDrillInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // Leaving the mode has to leave the page open, or a theme switch made while
            // drilled in would strand every other group Collapsed.
            if (!(bool)e.NewValue) Close();
        }

        public static readonly DependencyProperty AlwaysOpenProperty =
            DependencyProperty.Register(nameof(AlwaysOpen), typeof(bool), typeof(SettingsGroup),
                new PropertyMetadata(false));

        /// <summary>
        /// Never becomes a destination. The group shows its rows on the list itself,
        /// for the one control on a page that is the reason the page exists and should
        /// not be a click away.
        /// </summary>
        /// <remarks>
        /// Set by a page, not by the theme, because which group matters that much is a
        /// fact about the page. It does nothing in a theme that has no drill-in.
        /// </remarks>
        public bool AlwaysOpen
        {
            get => (bool)GetValue(AlwaysOpenProperty);
            set => SetValue(AlwaysOpenProperty, value);
        }

        private static readonly DependencyPropertyKey IsOpenPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(IsOpen), typeof(bool), typeof(SettingsGroup),
                new PropertyMetadata(false));

        public static readonly DependencyProperty IsOpenProperty = IsOpenPropertyKey.DependencyProperty;

        /// <summary>This group is the one currently drilled into.</summary>
        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            private set => SetValue(IsOpenPropertyKey, value);
        }

        private static readonly DependencyPropertyKey IsDimmedPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(IsDimmed), typeof(bool), typeof(SettingsGroup),
                new PropertyMetadata(false, (d, _) => d.CoerceValue(VisibilityProperty)));

        public static readonly DependencyProperty IsDimmedProperty = IsDimmedPropertyKey.DependencyProperty;

        /// <summary>Another group is open, so this one is out of the way.</summary>
        public bool IsDimmed
        {
            get => (bool)GetValue(IsDimmedProperty);
            private set => SetValue(IsDimmedPropertyKey, value);
        }

        /// <summary>
        /// Raised when any group opens or closes, so siblings can step aside. Static
        /// because the groups on a page are siblings in XAML with nothing between them
        /// that knows about all of them.
        /// </summary>
        private static event Action<SettingsGroup?>? OpenChanged;

        private static SettingsGroup? _open;

        /// <summary>The subpage currently open anywhere in the window, or null.</summary>
        public static SettingsGroup? OpenGroup => _open;

        /// <summary>
        /// Raised when the open subpage changes, for chrome that lives outside the page.
        /// </summary>
        /// <remarks>
        /// Separate from the private <c>OpenChanged</c> above, which exists so siblings
        /// can step aside and is nobody else's business. This one is what the title bar
        /// listens to: it is the only way anything outside a page can know which subpage
        /// is showing, now that a subpage no longer states its own name.
        /// </remarks>
        public static event Action<SettingsGroup?>? OpenGroupChanged;

        /// <summary>Opens this group, closing whichever was open.</summary>
        public void Open()
        {
            if (!DrillIn || _open == this) return;
            _open = this;
            OpenChanged?.Invoke(_open);
            OpenGroupChanged?.Invoke(_open);
        }

        /// <summary>Returns the page to the list of groups.</summary>
        public static void Close()
        {
            if (_open is null) return;
            _open = null;
            OpenChanged?.Invoke(null);
            OpenGroupChanged?.Invoke(null);
        }

        private void OnOpenChanged(SettingsGroup? open)
        {
            IsOpen = open == this;

            // Only groups on the same page step aside. Two pages are alive at once
            // during a navigation transition, and the one being left should not be
            // rearranged on its way out.
            bool samePage = open is not null && ReferenceEquals(PageRoot(), open.PageRoot());
            IsDimmed = open is not null && open != this && samePage;
        }

        /// <summary>
        /// The page this group sits on, used to tell siblings from strangers. The
        /// nearest UserControl ancestor is the page: every page in the app is one.
        /// </summary>
        private DependencyObject? PageRoot() => PageRootOf(this);

        private static DependencyObject? PageRootOf(DependencyObject? from)
        {
            DependencyObject? node = from;
            DependencyObject? page = null;
            while (node is not null)
            {
                if (node is System.Windows.Controls.UserControl) page = node;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node)
                    ?? LogicalTreeHelper.GetParent(node);
            }
            return page;
        }

        /// <summary>
        /// Marks page content that is not a group but belongs to the list of groups, so
        /// it stands aside while one of them is open.
        /// </summary>
        /// <remarks>
        /// Groups step aside for each other on their own. Anything else a page puts
        /// among them does not: the status tiles above Battery's groups stayed on screen
        /// inside every subpage, over a heading that had been retitled to name the
        /// subpage, so the page read as a subpage with another page's summary stuck to
        /// the top of it.
        ///
        /// <para>Set this on the container, not on each tile, and only on content whose
        /// Visibility is not otherwise bound: this writes that property directly, and a
        /// local value would win over a page's own binding. Groups use coercion instead,
        /// which is worth its complexity there because pages do bind their visibility to
        /// the hardware they need.</para>
        /// </remarks>
        public static readonly DependencyProperty HideInSubpageProperty =
            DependencyProperty.RegisterAttached(
                "HideInSubpage", typeof(bool), typeof(SettingsGroup),
                new PropertyMetadata(false, OnHideInSubpageChanged));

        public static void SetHideInSubpage(DependencyObject element, bool value)
            => element.SetValue(HideInSubpageProperty, value);

        public static bool GetHideInSubpage(DependencyObject element)
            => (bool)element.GetValue(HideInSubpageProperty);

        /// <summary>
        /// The handler each marked element listens with, so it can be taken off again.
        /// </summary>
        /// <remarks>
        /// Keyed weakly on the element. The event is static and outlives every page, and
        /// a strong table here would hold each page that ever carried such content for
        /// the life of the process - the tray release exists to avoid exactly that.
        /// </remarks>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, Action<SettingsGroup?>> _hideHandlers = new();

        private static void OnHideInSubpageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element) return;

            if (_hideHandlers.TryGetValue(element, out var existing))
            {
                OpenChanged -= existing;
                _hideHandlers.Remove(element);
            }

            if (!(bool)e.NewValue) return;

            void Apply(SettingsGroup? open)
            {
                // Only for the page this content is on. Two pages are alive at once
                // during a navigation transition, and the one being left must not be
                // rearranged on its way out - the same rule the groups follow.
                bool hidden = open is not null
                    && ReferenceEquals(PageRootOf(element), PageRootOf(open));
                element.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            }

            _hideHandlers.Add(element, Apply);
            OpenChanged += Apply;

            // Whatever is open right now, not whatever was open when this page was last
            // built: a cached page comes back carrying the state it was left in.
            Apply(_open);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (GetTemplateChild("PART_Back") is System.Windows.Controls.Primitives.ButtonBase back)
            {
                back.Click -= OnBackClick;
                back.Click += OnBackClick;
            }
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            Close();
            e.Handled = true;
        }

        /// <summary>
        /// While closed, the group is a single pressable panel, so any click inside it
        /// is a press on that panel. While open it is a list of live rows and this must
        /// keep its hands off them.
        /// </summary>
        protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (!DrillIn || AlwaysOpen || IsOpen || e.Handled) return;
            if (string.IsNullOrEmpty(Header)) return;

            Open();
            e.Handled = true;
        }

        protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            base.OnItemsChanged(e);

            foreach (object? item in Items)
            {
                if (item is not FrameworkElement element) continue;

                // Re-subscribing is harmless: the same handler instance for the same
                // element replaces nothing, so guard with an unsubscribe first.
                element.IsVisibleChanged -= OnChildVisibilityChanged;
                element.IsVisibleChanged += OnChildVisibilityChanged;
            }

            UpdateDividers();
        }

        private void OnChildVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateDividers();

        private void UpdateDividers()
        {
            bool isFirstVisible = true;

            for (int index = 0; index < Items.Count; index++)
            {
                object? item = Items[index];
                FrameworkElement? element = item as FrameworkElement;

                // Data-bound groups generate a ContentPresenter whose template contains
                // the SettingsRow. Static groups already give us the row directly.
                if (element is null && ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container)
                    element = FindSettingsRow(container);
                if (element is null) continue;

                element.IsVisibleChanged -= OnChildVisibilityChanged;
                element.IsVisibleChanged += OnChildVisibilityChanged;
                if (element.Visibility != Visibility.Visible) continue;

                if (element is SettingsRow row)
                    row.ShowDivider = !isFirstVisible;

                isFirstVisible = false;
            }
        }

        private static SettingsRow? FindSettingsRow(DependencyObject root)
        {
            if (root is SettingsRow row) return row;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
                if (FindSettingsRow(System.Windows.Media.VisualTreeHelper.GetChild(root, index)) is { } nested)
                    return nested;
            return null;
        }
    }
}
