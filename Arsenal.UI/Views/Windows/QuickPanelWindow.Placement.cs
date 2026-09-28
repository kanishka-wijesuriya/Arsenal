using Arsenal.Application.Models;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Arsenal.UI.Views.Windows
{
    public partial class QuickPanelWindow
    {
        /// <summary>The work area the panel is anchored in, in DIPs.</summary>
        private FlyoutBounds _anchorWorkArea;

        /// <summary>Set once the first capture has run, whatever it found.</summary>
        private bool _anchorCaptured;

        /// <summary>Which edge of that work area the taskbar, and so the tray, is on.</summary>
        private TaskbarEdge _anchorEdge = TaskbarEdge.Bottom;

        /// <summary>Height of the work area the panel is anchored in, in DIPs.</summary>
        private double _anchorHeight;

        /// <summary>
        /// The destination monitor's scale, captured with the anchor. The placement is
        /// computed in that monitor's DIPs and has to be turned back into pixels with
        /// the same number - never with whatever scale the HWND currently carries.
        /// </summary>
        private double _anchorScale = 1;

        /// <summary>
        /// The same work area in physical pixels. WPF can round equal DIP offsets to
        /// different pixels on the two axes, so the final visual alignment uses this
        /// unscaled rectangle as its source of truth.
        /// </summary>
        private System.Drawing.Rectangle _anchorWorkingAreaPixels;

        /// <summary>
        /// Records the corner the panel is pinned to, and the edge that put it there.
        /// Captured once per open: the height animation re-places the window on every
        /// frame, and re-reading the cursor's screen each time would make the panel jump
        /// if the pointer crossed monitors mid-transition. Once per open is also what
        /// makes a taskbar moved while Arsenal is running take effect - the next press
        /// reads the new layout with nothing to restart.
        /// </summary>
        private void CaptureTrayAnchor()
        {
            var point = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(point);
            var workingArea = screen.WorkingArea;
            _anchorWorkingAreaPixels = workingArea;
            uint dpi = DestinationDpi(point);
            double scale = dpi > 0 ? dpi / 96d : 1d;
            _anchorScale = scale;
            _anchorWorkArea = new FlyoutBounds(
                workingArea.Left / scale,
                workingArea.Top / scale,
                workingArea.Right / scale,
                workingArea.Bottom / scale);
            _anchorHeight = workingArea.Height / scale;
            _anchorEdge = TaskbarEdgeForScreen(screen);
            _anchorCaptured = true;

            // Before anything measures: the card is glued to one edge of a window that is
            // taller than it is, and which edge that is changes what a detail page does
            // to the layout it grows into.
            ApplyCardAlignment();
        }

        /// <summary>
        /// The scale of the monitor the panel is about to open on.
        /// </summary>
        /// <remarks>
        /// Arsenal is PerMonitorV2 (<c>ApplicationHighDpiMode</c> in Arsenal.UI.csproj,
        /// applied by DpiAwareEntryPoint), so every monitor has its own scale and the
        /// panel has to be measured in the scale of the one it is going to. Reading it
        /// from the window instead answers for wherever the HWND happens to be standing
        /// - which, on the open after a display change, is still the monitor that has
        /// just gone away.
        /// </remarks>
        private uint DestinationDpi(System.Drawing.Point point)
        {
            var nativePoint = new PointI { X = point.X, Y = point.Y };
            IntPtr monitor = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero
                && GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out uint dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX;
            }

            return GetDpiForWindow(new WindowInteropHelper(this).EnsureHandle());
        }

        /// <summary>
        /// Glues the card to the edge of the window nearest the taskbar, so the window
        /// slack it floats in is always on the far side of it.
        /// </summary>
        private void ApplyCardAlignment()
        {
            VerticalAlignment alignment = TrayFlyout.TrayIsAtTheTop(_anchorEdge)
                ? VerticalAlignment.Top
                : VerticalAlignment.Bottom;

            if (PanelChrome.VerticalAlignment == alignment) return;
            PanelChrome.VerticalAlignment = alignment;
            PanelShadow.VerticalAlignment = alignment;

            // The overlay layer is sized to the card, so it has to be glued to the same
            // edge. Left behind, it sits over whatever transparent slack the window is
            // carrying and the scrim reads as a box beside the panel instead of on it.
            PanelOverlayLayer.VerticalAlignment = alignment;
        }

        /// <summary>
        /// Answers which edge the taskbar is docked to for the monitor the panel is about
        /// to open on.
        /// </summary>
        /// <remarks>
        /// The shell reports the position of the primary taskbar only, so that answer is
        /// used directly when that bar is actually on this monitor - it is exact, and it
        /// stays right for an auto-hidden bar, which reserves no work area to infer from.
        /// On any other monitor the reserved space is the better signal, because that is
        /// what a secondary taskbar moves; the primary bar's edge is the fallback, since
        /// Windows docks every secondary taskbar to the same edge as the primary.
        /// </remarks>
        private static TaskbarEdge TaskbarEdgeForScreen(System.Windows.Forms.Screen screen)
        {
            if (EdgeOverride is TaskbarEdge forced) return forced;

            TaskbarEdge shellEdge = TaskbarEdge.Bottom;
            if (TryGetTaskbarPosition(out TaskbarEdge edge, out System.Drawing.Rectangle taskbar))
            {
                shellEdge = edge;
                if (taskbar.IntersectsWith(screen.Bounds)) return shellEdge;
            }

            return TrayFlyout.EdgeFromReservedSpace(screen.Bounds, screen.WorkingArea, shellEdge);
        }

        /// <summary>
        /// Forces an edge for a <c>--quick-test</c> run, so all four layouts can be seen
        /// without restarting the shell. Null in every normal launch.
        /// </summary>
        private static readonly TaskbarEdge? EdgeOverride = ReadEdgeOverride();

        private static TaskbarEdge? ReadEdgeOverride()
        {
            const string Prefix = "--panel-edge=";
            string? value = Environment.GetCommandLineArgs()
                .FirstOrDefault(arg => arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
            if (value is null) return null;
            return Enum.TryParse(value[Prefix.Length..], ignoreCase: true, out TaskbarEdge edge)
                ? edge
                : null;
        }

        private static bool TryGetTaskbarPosition(out TaskbarEdge edge, out System.Drawing.Rectangle bounds)
        {
            edge = TaskbarEdge.Bottom;
            bounds = System.Drawing.Rectangle.Empty;

            var data = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>() };
            if (SHAppBarMessage(AbmGetTaskbarPos, ref data) == IntPtr.Zero) return false;

            edge = data.uEdge switch
            {
                AbeLeft => TaskbarEdge.Left,
                AbeTop => TaskbarEdge.Top,
                AbeRight => TaskbarEdge.Right,
                _ => TaskbarEdge.Bottom
            };
            bounds = System.Drawing.Rectangle.FromLTRB(
                data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        /// <summary>
        /// Gap between the visible card and the work area, applied equally to the taskbar
        /// edge and the screen edge so the panel is seated in its corner rather than
        /// floating off it.
        /// </summary>
        private const double TrayGap = 11;

        /// <summary>
        /// The most <see cref="MatchTaskbarGapToScreenGap"/> may move the window. It
        /// equalises the device-pixel rounding of one DIP gap, which is a pixel or two.
        /// </summary>
        private const int MaxGapCorrection = 3;

        /// <summary>
        /// The size to seat in the corner, in the DIPs of the monitor being opened on.
        /// </summary>
        /// <remarks>
        /// Not <see cref="FrameworkElement.ActualWidth"/>. A cross-monitor DPI change
        /// resizes the HWND in pixels, and WPF converts that new pixel size back into
        /// DIPs using the scale it was carrying before the change: the 452x771 pixel
        /// window this panel becomes on a 100% display is reported as 301x514, because
        /// those pixels were divided by the laptop panel's 1.5. The placement then
        /// reserves two thirds of the room the card needs and seats it that far past the
        /// bottom-right corner. WPF catches up before the next open, which is why only
        /// the first one after a display switch is wrong.
        ///
        /// <para>The native rectangle is the size the window really is, in the same
        /// pixels the placement is applied in, so the anchor's own scale turns it into
        /// the DIPs the arithmetic wants. The properties are the fallback for the first
        /// open of all, before the window has a handle to measure.</para>
        /// </remarks>
        private (double Width, double Height) MeasuredWindowSize()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (_anchorScale > 0
                && hwnd != IntPtr.Zero
                && GetWindowRect(hwnd, out NativeRect bounds)
                && bounds.Right > bounds.Left
                && bounds.Bottom > bounds.Top)
            {
                return ((bounds.Right - bounds.Left) / _anchorScale,
                        (bounds.Bottom - bounds.Top) / _anchorScale);
            }

            return (ActualWidth > 0 ? ActualWidth : Width,
                    ActualHeight > 0 ? ActualHeight : 650);
        }

        public void PositionNearTray()
        {
            if (!_anchorCaptured) CaptureTrayAnchor();

            (double panelWidth, double panelHeight) = MeasuredWindowSize();

            // The card is inset from the window by the gutter its shadow needs, so the
            // window is placed that much nearer the edge to land the card on the gap.
            // Read from the live margin: shrinking the gutter must not move the card.
            Thickness margin = MotionRoot.Margin;
            FlyoutPlacement placement = TrayFlyout.Place(
                _anchorEdge,
                _anchorWorkArea,
                panelWidth,
                panelHeight,
                TrayGap,
                new FlyoutGutter(margin.Left, margin.Top, margin.Right, margin.Bottom));

            _enterX = placement.EnterX;
            _enterY = placement.EnterY;

            // Moved as pixels rather than through Left and Top. Those are DIPs, and WPF
            // turns them into pixels with the scale of the monitor the HWND is standing
            // on - which on the open after a display change is the one that has just
            // gone away, not the one this placement was measured for. Multiplying by the
            // anchor's own scale and moving the HWND keeps the two ends in one space.
            MoveToPixels(placement.Left * _anchorScale, placement.Top * _anchorScale);

            // 11 DIPs at 150% is 16.5 physical pixels. Depending on the fractional
            // window origin, WPF can resolve that as 16px on one axis and 17px on the
            // other. Measure the rendered card after layout and move only the native
            // HWND by the residual pixel delta; this guarantees the gap against the
            // taskbar is exactly the gap against the screen edge at every DPI.
            UpdateLayout();
            MatchTaskbarGapToScreenGap();
        }

        /// <summary>
        /// Puts the window's top-left corner on a physical pixel, whatever scale the
        /// window currently believes it is drawn at.
        /// </summary>
        private void MoveToPixels(double left, double top)
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                (int)Math.Round(left, MidpointRounding.AwayFromZero),
                (int)Math.Round(top, MidpointRounding.AwayFromZero),
                0,
                0,
                SetWindowPositionFlags.NoSize | SetWindowPositionFlags.NoZOrder | SetWindowPositionFlags.NoActivate);
        }

        /// <summary>
        /// Parks the HWND on the monitor the panel is about to open on, before a single
        /// DIP of it is measured or sized.
        /// </summary>
        /// <remarks>
        /// Width and Height are DIPs, and WPF turns them into pixels with the scale of
        /// the monitor the HWND is standing on. When the panel is opened on a second
        /// monitor while the window still rests on the first - both attached, so nothing
        /// has relocated it - a 452 DIP card sized against a 150% laptop panel is 678
        /// physical pixels wide, and the placement that follows reserves the 452 the
        /// 100% display calls for. Moving first makes Windows deliver WM_DPICHANGED
        /// here, where nothing has been measured yet, so every DIP converted after this
        /// point uses the scale of the monitor the panel is actually opening on.
        ///
        /// <para>Switching displays is the other case, and it does not come through
        /// here: Windows has already dragged the window onto the surviving monitor and
        /// changed its scale before the panel is asked to open. What is stale there is
        /// the size WPF reports, which <see cref="MeasuredWindowSize"/> answers.</para>
        /// </remarks>
        private void MoveToAnchorMonitor()
        {
            if (!_anchorCaptured || _anchorWorkingAreaPixels.IsEmpty) return;

            if (new WindowInteropHelper(this).EnsureHandle() == IntPtr.Zero) return;

            // The scale is the test, not the coordinates. Work areas from two displays
            // overlap - a laptop panel and the external that replaces it both start at
            // the origin - so an origin inside the destination's rectangle is no
            // evidence the window is on the destination. A window already converting
            // DIPs with the anchor's scale has nothing to correct, and moving it would
            // only throw away the corner the last open settled on.
            double current = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (Math.Abs(current - _anchorScale) < 0.001) return;

            // Anywhere inside the destination work area will do - the placement is
            // computed and applied a few lines later. The corner is chosen because the
            // window is guaranteed to overlap the monitor from there whatever size it
            // currently is, which is what makes Windows treat it as having moved.
            MoveToPixels(_anchorWorkingAreaPixels.Left, _anchorWorkingAreaPixels.Top);
        }

        /// <summary>
        /// Equalises the two visible gaps in physical pixels, after WPF has rounded the
        /// placement above onto the device grid.
        /// </summary>
        /// <remarks>
        /// The gap against the screen edge is the reference and the gap against the
        /// taskbar is moved onto it, because the screen edge is the one the eye has a
        /// straight line to compare against. Which axis is which follows the taskbar: a
        /// bar along the bottom leaves the right-hand gap as the reference, a bar down
        /// either side leaves the bottom one.
        /// </remarks>
        private void MatchTaskbarGapToScreenGap()
        {
            if (_anchorWorkingAreaPixels.IsEmpty || PanelChrome.ActualWidth <= 0 || PanelChrome.ActualHeight <= 0)
                return;

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            System.Windows.Point topLeft = PanelChrome.PointToScreen(new System.Windows.Point(0, 0));
            System.Windows.Point bottomRight = PanelChrome.PointToScreen(
                new System.Windows.Point(PanelChrome.ActualWidth, PanelChrome.ActualHeight));

            // Opening/closing motion is a render-only translation. Remove its current
            // device-pixel contribution so placement is based on the card's resting
            // geometry even when PositionNearTray runs before the first animation.
            double shiftX = PanelTransform.X * dpi.DpiScaleX;
            double shiftY = PanelTransform.Y * dpi.DpiScaleY;
            topLeft.X -= shiftX;
            topLeft.Y -= shiftY;
            bottomRight.X -= shiftX;
            bottomRight.Y -= shiftY;

            double leftGap = topLeft.X - _anchorWorkingAreaPixels.Left;
            double rightGap = _anchorWorkingAreaPixels.Right - bottomRight.X;
            double topGap = topLeft.Y - _anchorWorkingAreaPixels.Top;
            double bottomGap = _anchorWorkingAreaPixels.Bottom - bottomRight.Y;

            // Placement already keeps an oversized window's origin on screen. Do not
            // undo that safety by pulling the bottom-aligned card towards the taskbar.
            // The responsive viewport normally prevents this case; the guard also
            // protects the first layout frame and any unexpected oversized content.
            if (leftGap < 0 || rightGap < 0 || topGap < 0 || bottomGap < 0) return;

            int horizontal = 0;
            int vertical = 0;
            switch (_anchorEdge)
            {
                case TaskbarEdge.Bottom:
                    vertical = Delta(bottomGap, rightGap);
                    break;
                case TaskbarEdge.Top:
                    vertical = -Delta(topGap, rightGap);
                    break;
                case TaskbarEdge.Left:
                    horizontal = -Delta(leftGap, bottomGap);
                    break;
                case TaskbarEdge.Right:
                    horizontal = Delta(rightGap, bottomGap);
                    break;
            }

            if (horizontal == 0 && vertical == 0) return;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out NativeRect bounds)) return;

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                bounds.Left + horizontal,
                bounds.Top + vertical,
                0,
                0,
                SetWindowPositionFlags.NoSize | SetWindowPositionFlags.NoZOrder | SetWindowPositionFlags.NoActivate);

            // How far the window has to move, in whole device pixels, for the first gap
            // to equal the second. Positive shrinks the first gap.
            //
            // Capped, because this only ever corrects the rounding of one DIP gap onto
            // the device grid - a pixel, two at the highest scales Windows offers. The
            // gaps above come from PointToScreen, which reads the card through the same
            // stale scale that MeasuredWindowSize exists to avoid, and an unbounded
            // delta would let one bad frame throw the panel across the display rather
            // than nudge it onto the grid.
            static int Delta(double gap, double reference)
            {
                int delta = (int)Math.Round(gap - reference, MidpointRounding.AwayFromZero);
                return Math.Clamp(delta, -MaxGapCorrection, MaxGapCorrection);
            }
        }

        /// <summary>
        /// Fits the tile page to the current monitor and locks the transparent HWND to
        /// that size for this open. Detail pages remain compact because their visible
        /// card is bottom-aligned inside these stable transparent bounds.
        /// </summary>
        private void LockNativeViewport()
        {
            // The height below is recomputed from the content on every open, so it
            // survives a scale change on its own. The width is a constant nothing ever
            // wrote back, and WM_DPICHANGED resizes the HWND in pixels: carried from a
            // 150% panel to a 100% monitor, the same 678 physical pixels stop being 452
            // DIPs and become 678, and the card opens half as wide again. Restored
            // before anything measures, because every row in it is measured against
            // this width.
            if (_nativeViewportWidth > 0 && Math.Abs(Width - _nativeViewportWidth) > 0.5)
            {
                Width = _nativeViewportWidth;
                UpdateLayout();
            }

            // Before the height is read: a second page of tiles must be clipped away
            // first, or the locked window would be sized to hold every one of them.
            FitTilePageViewport();
            FitMainView();
            UpdateLayout();

            // Build the tile surface while the panel is settling after its first layout,
            // not on the same input frame that starts a page switch. Keeping this cache
            // warm turns every later page transition into one compositor translation.
            SetTileBitmapCache(true);

            double chrome = PanelChrome.Padding.Top + PanelChrome.Padding.Bottom
                + PanelChrome.BorderThickness.Top + PanelChrome.BorderThickness.Bottom;
            _mainHostHeight = Math.Max(1, MainView.DesiredSize.Height);
            _mainChromeHeight = _mainHostHeight + chrome;
            _nativeViewportHeight = _mainChromeHeight
                + MotionRoot.Margin.Top + MotionRoot.Margin.Bottom;

            SizeToContent = System.Windows.SizeToContent.Manual;
            Height = _nativeViewportHeight;
            PanelChrome.Height = _mainChromeHeight;
            TransitionHost.Height = _mainHostHeight;
            UpdateLayout();
        }

        /// <summary>
        /// Lets the normal layout stand on roomy displays. On a short work area it caps
        /// the main surface and gives it a scrollbar, keeping the header and every
        /// control reachable instead of clipping the top of the bottom-aligned card.
        /// </summary>
        private void FitMainView()
        {
            double width = Math.Max(1, TransitionHost.ActualWidth);
            double chrome = PanelChrome.Padding.Top + PanelChrome.Padding.Bottom
                + PanelChrome.BorderThickness.Top + PanelChrome.BorderThickness.Bottom;

            MainView.MaxHeight = double.PositiveInfinity;
            MainContent.InvalidateMeasure();
            MainContent.Measure(new System.Windows.Size(width, double.PositiveInfinity));

            MainView.MaxHeight = Math.Max(160, MaxCardHeight() - chrome);
            MainView.InvalidateMeasure();
            MainView.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        }

        private int _viewportRefitVersion;

        private void QueueViewportRefit()
        {
            int version = ++_viewportRefitVersion;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (version != _viewportRefitVersion || !IsVisible || _nativeViewportHeight <= 0) return;

                CaptureTrayAnchor();
                MoveToAnchorMonitor();
                LockNativeViewport();
                PositionNearTray();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// The tallest the visible card may be drawn: the work area it is anchored in,
        /// less the gutter its shadow needs and the gap it keeps off the taskbar.
        /// </summary>
        private double MaxCardHeight()
        {
            double workArea = _anchorHeight > 0 ? _anchorHeight : SystemParameters.WorkArea.Height;
            double usable = Math.Min(workArea, MaxHeight);
            return Math.Max(240, usable - MotionRoot.Margin.Top - MotionRoot.Margin.Bottom - TrayGap);
        }

        /// <summary>
        /// Caps the option list so a long one scrolls rather than growing the card past
        /// the screen.
        /// </summary>
        /// <remarks>
        /// The tile picker lists every tile the machine supports, which is several times
        /// what a flyout can show. Uncapped, the card measured taller than the work area,
        /// and because it is anchored to the bottom of the screen the excess ran off the
        /// top - the list appeared clipped at the top and parked at its last entry, with
        /// no way to reach the first.
        /// </remarks>
        private void FitDetailList()
        {
            // Measured unconstrained first, so the header's real height is known before
            // the remaining room is handed to the list.
            DetailScroll.MaxHeight = double.PositiveInfinity;
            DetailView.InvalidateMeasure();
            DetailView.Measure(new System.Windows.Size(
                Math.Max(1, TransitionHost.ActualWidth), double.PositiveInfinity));

            double chrome = PanelChrome.Padding.Top + PanelChrome.Padding.Bottom
                + PanelChrome.BorderThickness.Top + PanelChrome.BorderThickness.Bottom;
            DetailScroll.MaxHeight = Math.Max(160, MaxCardHeight() - chrome - DetailHeader.DesiredSize.Height);
        }

        /// <summary>
        /// Grows the transparent window when the card no longer fits inside it. It never
        /// shrinks: the card is bottom-aligned within the window, so spare room above it
        /// is invisible, while too little clips whatever is on screen.
        /// </summary>
        private void EnsureViewportFits(double cardHeight)
        {
            double needed = cardHeight + MotionRoot.Margin.Top + MotionRoot.Margin.Bottom;
            if (needed <= _nativeViewportHeight) return;

            _nativeViewportHeight = needed;
            Height = needed;
            PositionNearTray();
        }

        /// <summary>
        /// Re-fits the card after the tile grid gains or loses a row.
        /// </summary>
        /// <remarks>
        /// <see cref="LockNativeViewport"/> pins the card and the transparent window to
        /// the height the tile page measured on the first open, which is what keeps the
        /// detail transition from resizing the HWND. A tile added or removed is the one
        /// thing that legitimately changes that height, so the pinned values are
        /// recomputed here - and the window is allowed to grow, though never to shrink,
        /// because the card is bottom-aligned inside it and a window left slightly tall
        /// is invisible while one left short clips the grid.
        /// </remarks>
        private void RemeasureMainView()
        {
            if (_nativeViewportHeight <= 0) return;
            if (_viewTransitionRunning || _detailShown)
            {
                // Not now: a transition owns both heights while it runs, and the tile page
                // is not the one on screen anyway. It gets re-fitted on the way back.
                _remeasureWhenSettled = true;
                return;
            }
            if (TransitionHost.ActualWidth <= 0) return;

            _remeasureWhenSettled = false;

            double padding = Math.Max(0, PanelChrome.ActualHeight - TransitionHost.ActualHeight);

            FitMainView();
            MainView.InvalidateMeasure();
            MainView.Measure(new System.Windows.Size(TransitionHost.ActualWidth, double.PositiveInfinity));
            _mainHostHeight = Math.Max(1, MainView.DesiredSize.Height);
            _mainChromeHeight = _mainHostHeight + padding;

            PanelChrome.BeginAnimation(HeightProperty, null);
            TransitionHost.BeginAnimation(HeightProperty, null);
            TransitionHost.Height = _mainHostHeight;
            PanelChrome.Height = _mainChromeHeight;

            EnsureViewportFits(_mainChromeHeight);
            UpdateLayout();
            PositionNearTray();
        }
    }
}
