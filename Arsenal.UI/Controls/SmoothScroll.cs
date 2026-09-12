using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// Gives a WPF ScrollViewer smooth scrolling behavior matching Windows 11 shells and modern browsers:
    /// mouse wheel notches chase a target smoothly with natural exponential deceleration, while precision-touchpad
    /// gestures scroll with direct physical 1:1 finger tracking at native presentation rates without artificial lag.
    /// </summary>
    public static class SmoothScroll
    {
        /// <summary>
        /// WPF's own ScrollViewer moves 16 DIP per line for a mouse wheel notch, so with the
        /// default three-line setting a notch travels 48 DIP.
        /// </summary>
        private const double PixelsPerLine = 16d;

        private const double WheelDeltaPerNotch = 120d;

        private const double OffsetEpsilon = 0.001d;

        private const double TouchpadGestureTimeoutMs = 150d;

        private const uint MouseEventfFromTouch = 0xFF515700;
        private const uint SignatureMask = 0xFFFFFF00;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern nint GetMessageExtraInfo();

        /// <summary>
        /// Time constant of the wheel chase: the distance still to go falls by 1/e every
        /// this many milliseconds.
        /// </summary>
        /// <remarks>
        /// Stated in time rather than as a fraction per frame, because the curve is a
        /// property of the motion and not of the refresh rate - two 8ms frames have to
        /// cover exactly the ground one 16ms frame does. 67ms is the precise equivalent of
        /// the 22%-per-60Hz-frame this shipped with, so the wheel feel is unchanged.
        /// </remarks>
        private const double MouseWheelResponseMs = 67.08d;

        /// <summary>
        /// Time constant for continuous touchpad tracking: 16ms smooths out 125Hz touchpad
        /// packets across 240Hz presentation frames without adding perceptible finger lag.
        /// </summary>
        private const double TouchpadResponseMs = 16d;

        /// <summary>
        /// The floor on how far one frame may move while a wheel chase is still running.
        /// Kept sub-pixel (0.02 DIP) so high-refresh (240Hz) panels draw smooth sub-pixel
        /// micro-steps instead of quantizing into 1-pixel staircase jumps.
        /// </summary>
        private const double MinVisibleStep = 0.02d;

        /// <summary>
        /// A frame this long is a hitch or a debugger break, not a frame. Clamping keeps
        /// one stall from teleporting the content most of the way to the target.
        /// </summary>
        private const double MaxFrameMs = 64d;
        private static long _lastTouchpadTimestamp;

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(SmoothScroll),
                new PropertyMetadata(false, OnIsEnabledChanged));

        private static readonly DependencyProperty AnimatorProperty =
            DependencyProperty.RegisterAttached(
                "Animator",
                typeof(ScrollChase),
                typeof(SmoothScroll),
                new PropertyMetadata(null));

        public static bool GetIsEnabled(DependencyObject element) =>
            (bool)element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject element, bool value) =>
            element.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            if (dependencyObject is not ScrollViewer viewer)
                return;

            if ((bool)args.OldValue)
            {
                viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
                viewer.PreviewMouseLeftButtonDown -= OnInterrupted;
                viewer.PreviewKeyDown -= OnInterrupted;
                viewer.Unloaded -= OnInterrupted;
                viewer.Loaded -= OnLoadedAttachBringIntoViewGuard;
                DetachBringIntoViewGuard(viewer);
                GetChase(viewer)?.Stop();
                viewer.ClearValue(AnimatorProperty);
            }

            if (!(bool)args.NewValue)
                return;

            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            // A scrollbar drag, an arrow key or the page going away are all competing
            // scroll sources. Leaving the chase running would let it fight for the offset.
            viewer.PreviewMouseLeftButtonDown += OnInterrupted;
            viewer.PreviewKeyDown += OnInterrupted;
            viewer.Unloaded += OnInterrupted;
            viewer.Loaded += OnLoadedAttachBringIntoViewGuard;
            AttachBringIntoViewGuard(viewer);
        }

        private static readonly RequestBringIntoViewEventHandler BringIntoViewHandler = OnRequestBringIntoView;

        /// <summary>The content element the guard is currently attached to.</summary>
        private static readonly DependencyProperty GuardedContentProperty =
            DependencyProperty.RegisterAttached(
                "GuardedContent",
                typeof(FrameworkElement),
                typeof(SmoothScroll),
                new PropertyMetadata(null));

        private static void OnLoadedAttachBringIntoViewGuard(object sender, RoutedEventArgs args)
        {
            if (sender is ScrollViewer viewer) AttachBringIntoViewGuard(viewer);
        }

        /// <summary>
        /// Puts the guard below on the viewer's content rather than on the viewer.
        /// </summary>
        /// <remarks>
        /// ScrollViewer answers RequestBringIntoView with a <em>class</em> handler, and
        /// class handlers run ahead of instance handlers on the same element - so a
        /// handler attached to the viewer has already been beaten to it and the page has
        /// moved. Sitting one level in, on the content, the bubbling event is seen while
        /// it is still on its way up. An inner scroller of its own still gets its class
        /// handler first, so lists inside a page keep scrolling their selection into view.
        /// </remarks>
        private static void AttachBringIntoViewGuard(ScrollViewer viewer)
        {
            // Content is often still unset when the style setter runs, which is why this
            // is also called again from Loaded.
            if (viewer.Content is not FrameworkElement content) return;
            if (ReferenceEquals(viewer.GetValue(GuardedContentProperty), content)) return;

            DetachBringIntoViewGuard(viewer);
            content.AddHandler(FrameworkElement.RequestBringIntoViewEvent, BringIntoViewHandler);
            viewer.SetValue(GuardedContentProperty, content);
        }

        private static void DetachBringIntoViewGuard(ScrollViewer viewer)
        {
            if (viewer.GetValue(GuardedContentProperty) is FrameworkElement previous)
                previous.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, BringIntoViewHandler);
            viewer.ClearValue(GuardedContentProperty);
        }

        /// <summary>
        /// Stops a click from scrolling the page out from under the pointer.
        /// </summary>
        /// <remarks>
        /// Pressing a control focuses it, and WPF answers a focus change by asking the
        /// nearest scroller to bring that control fully into view. For a button sitting
        /// part-way past the fold - which, in a list of cards, is most of them - that
        /// walks the page along on every single click, and the button ends up somewhere
        /// other than where it was aimed at.
        ///
        /// Keyboard navigation genuinely needs it: tabbing to something off-screen has to
        /// be able to reach it. So the request is honoured when the keyboard was the last
        /// thing the user touched, and dropped when it was the mouse or a finger - which
        /// also keeps it from fighting the chase animation below.
        /// </remarks>
        private static void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs args)
        {
            if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) return;
            args.Handled = true;
        }

        private static ScrollChase? GetChase(ScrollViewer viewer)
            => viewer.GetValue(AnimatorProperty) as ScrollChase;

        private static ScrollChase EnsureChase(ScrollViewer viewer)
        {
            if (GetChase(viewer) is { } existing)
                return existing;

            var chase = new ScrollChase(viewer);
            viewer.SetValue(AnimatorProperty, chase);
            return chase;
        }

        private static void OnInterrupted(object sender, EventArgs args)
        {
            if (sender is ScrollViewer viewer)
                GetChase(viewer)?.Cancel();
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
        {
            if (sender is not ScrollViewer viewer || args.Delta == 0)
                return;

            // Popup content routes its wheel through this ScrollViewer on the way down,
            // so handling it here would scroll the page instead of the open list.
            if (IsPopupCapturingInput())
                return;

            if (viewer.ScrollableHeight <= 0d)
                return;

            // With logical scrolling the offset is measured in items, not pixels, so the
            // pixel distances below would be nonsense - a three-line notch would jump
            // three whole items. Leave those to WPF's own item-at-a-time handling.
            if (viewer.CanContentScroll)
                return;

            int configuredLines = SystemParameters.WheelScrollLines;
            if (configuredLines == 0)
                return;

            long now = Stopwatch.GetTimestamp();
            double msSinceLastTouchpad = (now - _lastTouchpadTimestamp) * 1000d / Stopwatch.Frequency;

            bool isTouchSignature = ((uint)GetMessageExtraInfo() & SignatureMask) == MouseEventfFromTouch;
            bool isFractional = Math.Abs(args.Delta) % (int)WheelDeltaPerNotch != 0 || Math.Abs(args.Delta) < WheelDeltaPerNotch;
            bool isTouchpad = isTouchSignature || isFractional || (msSinceLastTouchpad < TouchpadGestureTimeoutMs);

            double distancePerNotch = configuredLines < 0
                ? viewer.ViewportHeight
                : configuredLines * PixelsPerLine;
            double distance = -(args.Delta / WheelDeltaPerNotch) * distancePerNotch;

            ScrollChase chase = EnsureChase(viewer);

            // Turning system animations off is a request for no interpolation at all, so
            // that setting gets the raw packet written straight to the offset.
            if (!SystemParameters.ClientAreaAnimation)
            {
                args.Handled = chase.JumpBy(distance);
                return;
            }

            if (isTouchpad)
            {
                _lastTouchpadTimestamp = now;
                args.Handled = chase.GlideBy(distance);
            }
            else
            {
                args.Handled = chase.NudgeBy(distance);
            }
        }

        /// <summary>
        /// True while a drop-down, context menu or any other popup owns mouse input.
        /// An in-page drag such as a slider thumb also captures the mouse, so the test
        /// is what holds the capture rather than that a capture exists at all.
        /// </summary>
        private static bool IsPopupCapturingInput()
        {
            if (Mouse.Captured is not DependencyObject captured)
                return false;

            // A ComboBox captures itself for as long as its list is up.
            if (captured is System.Windows.Controls.ComboBox { IsDropDownOpen: true })
                return true;

            // Menus and bare popups capture inside their own popup root, which tops a
            // visual tree of its own rather than the window the page lives in.
            if (captured is Visual visual)
            {
                DependencyObject root = visual;
                while (VisualTreeHelper.GetParent(root) is DependencyObject parent)
                    root = parent;

                return root is not Window;
            }

            return false;
        }

        /// <summary>
        /// Chases a target offset, covering a fixed fraction of whatever distance is left
        /// on every presentation frame.
        /// </summary>
        /// <remarks>
        /// Deliberately not a per-gesture eased animation. An ease owns a fixed start,
        /// end and duration, so a notch arriving mid-flight has to restart the curve -
        /// which breaks velocity and produced a measured 19px lurch followed by a tail of
        /// sub-pixel frames. A chase has no duration to restart: input only moves the
        /// target, and the distance left is the entire state, so continuous input reads as
        /// one continuous movement.
        /// </remarks>
        private sealed class ScrollChase
        {
            private readonly ScrollViewer _viewer;
            private double _target;
            private double _current;
            private long _lastTimestamp;
            private bool _running;
            private double _responseMs = MouseWheelResponseMs;
            private double _requestedOffset;
            private bool _hasPendingRequest;

            internal ScrollChase(ScrollViewer viewer) => _viewer = viewer;

            /// <summary>
            /// Applies the distance at once, with no chase.
            /// </summary>
            internal bool JumpBy(double distance)
            {
                double origin = _running ? _target : ReadOffset();
                Stop();
                return Apply(origin + distance);
            }

            /// <summary>
            /// Moves towards a target position with a time constant short enough to track
            /// continuous touchpad input without perceivable lag, but long enough to turn a
            /// stream of 125Hz packets into a smooth path on a 240Hz panel.
            /// </summary>
            internal bool GlideBy(double distance) => Chase(distance, TouchpadResponseMs);

            /// <summary>
            /// Moves the target with the loose wheel response.
            /// </summary>
            internal bool NudgeBy(double distance) => Chase(distance, MouseWheelResponseMs);

            /// <summary>
            /// Adding to the pending target rather than to the on-screen offset is what
            /// makes several quick packets travel their full combined distance instead of
            /// each one restarting from a position the previous had not reached yet.
            /// </summary>
            private bool Chase(double distance, double responseMs)
            {
                _responseMs = responseMs;
                double origin;
                if (!_running)
                {
                    origin = ReadOffset();
                }
                else
                {
                    // If reversing direction mid-flight, pivot immediately from current on-screen
                    // position rather than overshooting towards the old forward target.
                    double movingDirection = Math.Sign(_target - _current);
                    double inputDirection = Math.Sign(distance);
                    origin = (movingDirection != 0 && inputDirection != 0 && inputDirection != movingDirection)
                        ? _current
                        : _target;
                }

                double target = Clamp(origin + distance);

                if (Math.Abs(target - ReadOffset()) <= OffsetEpsilon)
                {
                    Stop();
                    return false;
                }

                _target = target;

                if (!_running)
                {
                    // Track our own position rather than re-reading VerticalOffset each
                    // frame: the viewer commits a frame late, and chasing a lagging
                    // reading would quantise the motion back into uneven steps.
                    _current = ReadOffset();
                    _lastTimestamp = Stopwatch.GetTimestamp();
                    _running = true;
                    CompositionTarget.Rendering += OnRendering;
                }

                return true;
            }

            internal void Stop()
            {
                if (!_running)
                    return;

                _running = false;
                CompositionTarget.Rendering -= OnRendering;
            }

            internal void Cancel()
            {
                _hasPendingRequest = false;
                Stop();
            }

            private void OnRendering(object? sender, EventArgs args)
            {
                long now = Stopwatch.GetTimestamp();
                double frameMs = (now - _lastTimestamp) * 1000d / Stopwatch.Frequency;
                _lastTimestamp = now;
                frameMs = Math.Clamp(frameMs, 0.1d, MaxFrameMs);

                double remaining = _target - _current;
                double distance = Math.Abs(remaining);

                // One time constant per unit of real time. Frames are not counted anywhere
                // in here, so the same gesture draws the same curve whether the panel
                // presents every 16ms or every 4ms - the faster one simply gets more of
                // the curve's intermediate positions drawn for it.
                double factor = 1d - Math.Exp(-frameMs / _responseMs);
                double step = Math.Max(distance * factor, MinVisibleStep);

                // The floor has caught up with what is left: finish on the target rather
                // than step past it and settle back.
                if (step >= distance)
                {
                    Apply(_target);
                    Stop();
                    return;
                }

                Apply(_current + (Math.Sign(remaining) * step));
            }

            /// <summary>
            /// Where the content is, counting a write that has not reached layout yet.
            /// </summary>
            /// <remarks>
            /// ScrollViewer.VerticalOffset only updates on the arrange pass, so every wheel
            /// or touchpad packet handled in the same input batch reads the same pre-gesture
            /// value - and each one that starts from there silently throws away the distance
            /// its predecessor asked for. Trusting our own last request until the viewer
            /// visibly moves keeps a burst of packets additive, while still deferring to
            /// anything else that scrolls the viewer, such as a page resetting to the top.
            /// </remarks>
            private double ReadOffset()
            {
                double actual = _viewer.VerticalOffset;

                // If the viewer has caught up to our request, or no request is pending,
                // the viewer's actual offset is authoritatively up to date.
                if (!_hasPendingRequest || Math.Abs(actual - _requestedOffset) <= OffsetEpsilon)
                {
                    _hasPendingRequest = false;
                    return actual;
                }

                // If the user stopped scrolling (>150ms since last input), trust actual offset.
                long now = Stopwatch.GetTimestamp();
                if ((now - _lastTouchpadTimestamp) * 1000d / Stopwatch.Frequency > TouchpadGestureTimeoutMs)
                {
                    _hasPendingRequest = false;
                    return actual;
                }

                // While a gesture is actively streaming, keep pending requests additive
                // so intermediate layout frames never discard queued distance.
                return _requestedOffset;
            }



            private bool Apply(double offset)
            {
                double target = Clamp(offset);
                _current = target;

                if (Math.Abs(target - ReadOffset()) <= OffsetEpsilon)
                    return false;

                _requestedOffset = target;
                _hasPendingRequest = true;
                _viewer.ScrollToVerticalOffset(target);
                return true;
            }

            private double Clamp(double offset)
                => Math.Clamp(offset, 0d, _viewer.ScrollableHeight);
        }
    }
}
