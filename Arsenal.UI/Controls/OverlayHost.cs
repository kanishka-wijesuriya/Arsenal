using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// Hosts a panel inside the main window rather than in a separate top-level window.
    ///
    /// Search and setup used to be real Windows, which meant they could be dragged away
    /// from the app they belong to and behaved like separate things on the taskbar and
    /// in alt-tab. As an overlay the panel is part of the window, so it moves with it
    /// and cannot be separated.
    ///
    /// Closes on Escape or a click on the scrim. Animation is driven from code for the
    /// same reason as <see cref="BusyOverlay"/>: style triggers start a new animation
    /// clock on every open and never release the last one.
    ///
    /// The scrim and the card animate on separate clocks rather than as one block, which
    /// is what lets the dim lead the card in and follow it out. See <see cref="Open"/>
    /// and <see cref="Close"/>.
    /// </summary>
    public class OverlayHost : System.Windows.Controls.ContentControl
    {
        private const int FrameRate = 120;

        /// <summary>Where the card rests before it opens, and where it falls back to.</summary>
        private const double RestScale = 0.94;
        private const double RestLift = 18;

        /// <summary>
        /// Closing pulls back rather than repeating the entrance backwards. A card that
        /// left along the path it arrived on reads as an undo; a shorter, smaller retreat
        /// reads as dismissal.
        /// </summary>
        private const double ExitScale = 0.972;
        private const double ExitLift = 10;

        private FrameworkElement? _root;
        private FrameworkElement? _card;
        private ScaleTransform? _scale;
        private TranslateTransform? _translate;
        private FrameworkElement? _scrim;

        /// <summary>Raised after the close animation and its clocks are released.</summary>
        public event Action? Closed;

        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(OverlayHost),
                new FrameworkPropertyMetadata(false,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOpenChanged));

        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }

        public static readonly DependencyProperty PanelMaxWidthProperty =
            DependencyProperty.Register(nameof(PanelMaxWidth), typeof(double), typeof(OverlayHost),
                new PropertyMetadata(680d));

        /// <summary>Cap on the card's width. The search palette and the setup wizard
        /// want very different canvases from the same host.</summary>
        public double PanelMaxWidth
        {
            get => (double)GetValue(PanelMaxWidthProperty);
            set => SetValue(PanelMaxWidthProperty, value);
        }

        public static readonly DependencyProperty PanelMaxHeightProperty =
            DependencyProperty.Register(nameof(PanelMaxHeight), typeof(double), typeof(OverlayHost),
                new PropertyMetadata(620d));

        /// <summary>Cap on the card's height.</summary>
        public double PanelMaxHeight
        {
            get => (double)GetValue(PanelMaxHeightProperty);
            set => SetValue(PanelMaxHeightProperty, value);
        }

        public static readonly DependencyProperty IsLightDismissEnabledProperty =
            DependencyProperty.Register(nameof(IsLightDismissEnabled), typeof(bool), typeof(OverlayHost),
                new PropertyMetadata(true));

        /// <summary>
        /// Whether a click on the scrim or Escape closes the panel. A palette should
        /// close that way; a multi-step wizard should not, because a stray click would
        /// throw away everything the user has answered so far.
        /// </summary>
        public bool IsLightDismissEnabled
        {
            get => (bool)GetValue(IsLightDismissEnabledProperty);
            set => SetValue(IsLightDismissEnabledProperty, value);
        }

        static OverlayHost()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(OverlayHost),
                new FrameworkPropertyMetadata(typeof(OverlayHost)));
        }

        public OverlayHost()
        {
            Visibility = Visibility.Collapsed;
            Focusable = false;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _root = GetTemplateChild("PART_Root") as FrameworkElement;
            _card = GetTemplateChild("PART_Card") as FrameworkElement;
            _scale = GetTemplateChild("PART_Scale") as ScaleTransform;
            _translate = GetTemplateChild("PART_Translate") as TranslateTransform;

            if (_scrim is not null) _scrim.MouseLeftButtonDown -= OnScrimMouseLeftButtonDown;
            _scrim = GetTemplateChild("PART_Scrim") as FrameworkElement;
            if (_scrim is not null) _scrim.MouseLeftButtonDown += OnScrimMouseLeftButtonDown;

            Settle(IsOpen);
        }

        /// <summary>Writes the resting values for an open or a closed overlay.</summary>
        private void Settle(bool open)
        {
            if (_root is not null) _root.Opacity = 1;
            if (_scrim is not null) _scrim.Opacity = open ? 1 : 0;
            if (_card is not null) _card.Opacity = open ? 1 : 0;

            if (_scale is not null)
            {
                double scale = open ? 1 : RestScale;
                _scale.ScaleX = scale;
                _scale.ScaleY = scale;
            }

            if (_translate is not null) _translate.Y = open ? 0 : RestLift;
        }

        private void OnScrimMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsLightDismissEnabled) IsOpen = false;

            // Handled either way: the click must not fall through to the page behind it.
            e.Handled = true;
        }

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var host = (OverlayHost)d;
            if ((bool)e.NewValue) host.Open();
            else host.Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && IsLightDismissEnabled)
            {
                IsOpen = false;
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private readonly FrameEase _scrimFade = new();
        private readonly FrameEase _cardFade = new();
        private readonly FrameEase _scaleX = new();
        private readonly FrameEase _scaleY = new();
        private readonly FrameEase _lift = new();

        /// <summary>
        /// The dim leads and the card follows.
        /// </summary>
        /// <remarks>
        /// The scrim starts first because it is what tells the eye the page behind has
        /// gone away; the card then arrives into a room that is already dark, which is
        /// the difference between a panel appearing and a panel being opened. Its own
        /// motion outlasts its fade so the last of the travel plays out while the card is
        /// already readable, and the settling is carried by a quintic rather than a cubic
        /// - over 18px the slower tail is what stops the arrival reading as a snap.
        /// </remarks>
        private void Open()
        {
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;

            // The host rests collapsed, and a collapsed element is never measured - so on
            // the very first open there is no template yet, no parts to animate, and the
            // panel simply appeared. It was the first appearance of every overlay that
            // popped, which is the one that matters most: the setup wizard is only ever
            // opened once. Applying the template here and putting it back to its closed
            // resting values gives the eases below something to move.
            if (_card is null)
            {
                ApplyTemplate();
                Settle(false);
            }

            // Measure and arrange the panel before the clocks start rather than on the
            // first animated frame. The wizard's first layout costs well over a frame, and
            // paying for it after the eases begin means the opening is already a third
            // spent by the time anything is composited - the entrance visibly starting
            // part-way through.
            UpdateLayout();

            // Focus inside the overlay so Escape and typing land here rather than on
            // whatever page is behind it.
            Dispatcher.BeginInvoke(new Action(() => MoveFocus(new TraversalRequest(FocusNavigationDirection.First))),
                System.Windows.Threading.DispatcherPriority.Input);

            if (_scrim is null || _card is null || _scale is null || _translate is null) return;

            _scrimFade.Start(_scrim.Opacity, 1, 260, FrameEase.CubicOut, v => _scrim.Opacity = v);

            _cardFade.Start(_card.Opacity, 1, 220, FrameEase.CubicOut, v => _card.Opacity = v, delayMs: 50);
            _scaleX.Start(_scale.ScaleX, 1, 420, FrameEase.QuinticOut, v => _scale.ScaleX = v, delayMs: 50);
            _scaleY.Start(_scale.ScaleY, 1, 420, FrameEase.QuinticOut, v => _scale.ScaleY = v, delayMs: 50);
            _lift.Start(_translate.Y, 0, 420, FrameEase.QuinticOut, v => _translate.Y = v, delayMs: 50);
        }

        /// <summary>
        /// The card leaves first, the dim lifts after it.
        /// </summary>
        /// <remarks>
        /// Reversed against <see cref="Open"/>: whichever layer moves first is the one
        /// being acted on, and closing is an act on the card rather than on the page
        /// behind it. The scrim outlives the card by a beat, so the page is never
        /// uncovered while the card is still on top of it. Everything here is quicker
        /// than the entrance - an arrival can be savoured, a dismissal cannot.
        /// </remarks>
        private void Close()
        {
            IsHitTestVisible = false;

            if (_scrim is null || _card is null || _scale is null || _translate is null)
            {
                Visibility = Visibility.Collapsed;
                Closed?.Invoke();
                return;
            }

            // The fade is symmetric while the retreat accelerates. An ease-in on opacity
            // held the card at full strength for most of its 180ms and then dropped it,
            // which is the shape of a panel being switched off rather than dismissed;
            // beginning to go at once is what makes it look let go of.
            _cardFade.Start(_card.Opacity, 0, 180, FrameEase.CubicInOut, v => _card.Opacity = v);
            _scaleX.Start(_scale.ScaleX, ExitScale, 220, FrameEase.CubicIn, v => _scale.ScaleX = v);
            _scaleY.Start(_scale.ScaleY, ExitScale, 220, FrameEase.CubicIn, v => _scale.ScaleY = v);
            _lift.Start(_translate.Y, ExitLift, 220, FrameEase.CubicIn, v => _translate.Y = v);

            _scrimFade.Start(
                _scrim.Opacity, 0, 220, FrameEase.CubicInOut,
                v => _scrim.Opacity = v,
                completed: () =>
                {
                    if (IsOpen) return;

                    Visibility = Visibility.Collapsed;
                    Settle(false);
                    Closed?.Invoke();
                },
                delayMs: 60);
        }
    }
}
