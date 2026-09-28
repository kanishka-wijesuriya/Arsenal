using Arsenal.UI.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using TranslateTransform = System.Windows.Media.TranslateTransform;
using System.Windows.Threading;

namespace Arsenal.UI.Views.Windows
{
    public partial class QuickPanelWindow
    {
        private bool _detailShown;
        private int _viewTransitionVersion;

        /// <summary>
        /// A view transition owns both heights until it finishes. A tile added from the
        /// picker changes the grid and closes the picker in the same breath, and without
        /// this the re-fit would set a height the transition then animated away from.
        /// </summary>
        private bool _viewTransitionRunning;
        private bool _remeasureWhenSettled;
        private double _nativeViewportHeight;

        /// <summary>
        /// The card's design width in DIPs, taken from XAML before the window has met a
        /// second monitor. Nothing else restores it, so a scale change would otherwise
        /// leave the HWND's pixel width reinterpreted at the new scale.
        /// </summary>
        private readonly double _nativeViewportWidth;
        private double _mainChromeHeight;
        private double _mainHostHeight;

        /// <summary>
        /// Moves between the tile grid and a detail list without ever resizing the
        /// layered native window. Only the bottom-aligned card and its clipped viewport
        /// morph, so WPF cannot expose an intermediate full-height surface or move the
        /// tray anchor while composing a frame.
        /// </summary>
        private void TransitionToView(bool showDetail)
        {
            if (_detailShown == showDetail) return;
            _detailShown = showDetail;
            _viewTransitionRunning = true;
            int transitionVersion = ++_viewTransitionVersion;

            FrameworkElement incoming = showDetail ? DetailView : MainView;
            FrameworkElement outgoing = showDetail ? MainView : DetailView;
            TranslateTransform incomingShift = showDetail ? DetailViewShift : MainViewShift;
            TranslateTransform outgoingShift = showDetail ? MainViewShift : DetailViewShift;

            StopViewTransitionEases();
            StopViewAnimation(incoming, incomingShift);
            StopViewAnimation(outgoing, outgoingShift);

            double currentHostHeight = TransitionHost.ActualHeight;
            double currentChromeHeight = PanelChrome.ActualHeight;
            PanelChrome.Height = currentChromeHeight;
            TransitionHost.Height = currentHostHeight;

            // Put the incoming tree into layout first. Detail option containers are
            // generated after the collection changes, so measuring synchronously here
            // can see only the header and clip the option list.
            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 0.001;
            incoming.IsHitTestVisible = true;
            outgoing.IsHitTestVisible = false;
            UpdateLayout();

            // Both trees are static during the short cross-fade. Prime retained surfaces
            // before the Background callback starts the clocks, so opacity, translation,
            // and viewport clipping do not repeatedly rasterise every control.
            SetTransitionCache(incoming, true);
            SetTransitionCache(outgoing, true);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (transitionVersion != _viewTransitionVersion) return;

                // Measure after WPF has generated and bound every incoming item. The
                // explicit host still clips the nearly transparent view until motion.
                if (showDetail) FitDetailList();
                incoming.InvalidateMeasure();
                incoming.Measure(new System.Windows.Size(
                    Math.Max(1, TransitionHost.ActualWidth), double.PositiveInfinity));
                double targetHostHeight = Math.Max(1, incoming.DesiredSize.Height);
                double targetChromeHeight = targetHostHeight
                    + Math.Max(0, currentChromeHeight - currentHostHeight);

                // Before the animation, so the card's bottom edge stays put while the
                // window's top edge moves instead of the card jumping at the end.
                EnsureViewportFits(targetChromeHeight);

                incomingShift.Y = showDetail ? 8 : -6;

                _chromeHeightEase.Start(
                    currentChromeHeight, targetChromeHeight, 240,
                    Controls.FrameEase.CubicInOut,
                    h => PanelChrome.Height = h,
                    completed: () => CompleteViewTransition(
                        transitionVersion, incoming, outgoing, incomingShift, outgoingShift,
                        targetChromeHeight, targetHostHeight));

                _hostHeightEase.Start(
                    currentHostHeight, targetHostHeight, 240,
                    Controls.FrameEase.CubicInOut,
                    h => TransitionHost.Height = h);

                _outgoingFade.Start(
                    outgoing.Opacity, 0, 100,
                    Controls.FrameEase.CubicOut,
                    v => outgoing.Opacity = v);

                _outgoingSlide.Start(
                    outgoingShift.Y, showDetail ? -4 : 4, 120,
                    Controls.FrameEase.CubicOut,
                    y => outgoingShift.Y = y);

                _incomingFade.Start(
                    0.001, 1, 165,
                    Controls.FrameEase.CubicOut,
                    v => incoming.Opacity = v,
                    delayMs: 48);

                _incomingSlide.Start(
                    incomingShift.Y, 0, 190,
                    Controls.FrameEase.CubicOut,
                    y => incomingShift.Y = y,
                    delayMs: 32);
            }), DispatcherPriority.Background);
        }

        private void CompleteViewTransition(
            int transitionVersion,
            FrameworkElement incoming,
            FrameworkElement outgoing,
            TranslateTransform incomingShift,
            TranslateTransform outgoingShift,
            double targetChromeHeight,
            double targetHostHeight)
        {
            if (transitionVersion != _viewTransitionVersion) return;

            StopViewTransitionEases();
            StopViewAnimation(incoming, incomingShift);
            StopViewAnimation(outgoing, outgoingShift);
            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 1;
            incomingShift.Y = 0;
            // Keep the outgoing surface laid out and cached at zero opacity. Returning
            // to it therefore never has to reconstruct the full tile page mid-motion.
            outgoing.Visibility = Visibility.Visible;
            outgoing.Opacity = 0;
            outgoing.IsHitTestVisible = false;
            outgoingShift.Y = 0;
            incoming.IsHitTestVisible = true;

            SetTransitionCache(incoming, false);
            SetTransitionCache(outgoing, false);

            PanelChrome.Height = targetChromeHeight;
            TransitionHost.Height = targetHostHeight;

            _viewTransitionRunning = false;
            if (!_remeasureWhenSettled) return;
            _remeasureWhenSettled = false;
            RemeasureMainView();
        }

        private static void StopViewAnimation(FrameworkElement view, TranslateTransform shift)
        {
            double opacity = view.Opacity;
            double y = shift.Y;
            view.BeginAnimation(OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            view.Opacity = opacity;
            shift.X = 0;
            shift.Y = y;
        }

        private readonly Controls.FrameEase _chromeHeightEase = new();
        private readonly Controls.FrameEase _hostHeightEase = new();
        private readonly Controls.FrameEase _outgoingFade = new();
        private readonly Controls.FrameEase _outgoingSlide = new();
        private readonly Controls.FrameEase _incomingFade = new();
        private readonly Controls.FrameEase _incomingSlide = new();

        /// <summary>
        /// Drops every ease that composes a view transition, leaving each property at
        /// whatever value it currently holds.
        /// </summary>
        private void StopViewTransitionEases()
        {
            _chromeHeightEase.Stop();
            _hostHeightEase.Stop();
            _outgoingFade.Stop();
            _outgoingSlide.Stop();
            _incomingFade.Stop();
            _incomingSlide.Stop();
        }

        private static void SetTransitionCache(FrameworkElement view, bool cached)
        {
            // Transitions are animated natively without allocating and tearing down
            // intermediate bitmap caches.
        }

        /// <summary>
        /// Puts both views back to the tile page without animating, so a panel that was
        /// closed on a detail page does not play a transition the next time it opens.
        /// </summary>
        private void ResetToMainView()
        {
            EndTileDrag();
            if (IsVisible) SetTileBitmapCache(true);
            else SetTileBitmapCache(false);

            _detailShown = false;
            _viewTransitionRunning = false;
            _viewTransitionVersion++;

            PanelChrome.BeginAnimation(HeightProperty, null);
            TransitionHost.BeginAnimation(HeightProperty, null);
            if (_mainChromeHeight > 0) PanelChrome.Height = _mainChromeHeight;
            if (_mainHostHeight > 0) TransitionHost.Height = _mainHostHeight;

            foreach (var (view, shift) in new (FrameworkElement View, TranslateTransform Shift)[]
            {
                (MainView, MainViewShift),
                (DetailView, DetailViewShift)
            })
            {
                SetTransitionCache(view, false);
                view.BeginAnimation(OpacityProperty, null);
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                shift.X = 0;
                shift.Y = 0;
            }

            MainView.Visibility = Visibility.Visible;
            MainView.Opacity = 1;
            MainView.IsHitTestVisible = true;
            DetailView.Visibility = Visibility.Collapsed;
            DetailView.Opacity = 0;
            DetailView.IsHitTestVisible = false;

            // The heights restored above are the ones the grid last measured at. A tile
            // added or removed while a detail page was open makes them stale, and this is
            // the point where the grid becomes the visible page again.
            if (_remeasureWhenSettled)
            {
                _remeasureWhenSettled = false;
                RemeasureMainView();
            }
        }
    }
}
