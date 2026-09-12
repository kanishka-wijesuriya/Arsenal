using System.Drawing;

namespace Arsenal.Application.Models
{
    /// <summary>The screen edge the taskbar is docked to.</summary>
    public enum TaskbarEdge
    {
        Bottom,
        Left,
        Top,
        Right
    }

    /// <summary>A rectangle in device-independent pixels, the units WPF places windows in.</summary>
    public readonly record struct FlyoutBounds(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
    }

    /// <summary>
    /// Transparent slack the window carries around its visible card, per side. The card
    /// is inset from the window by whatever room its shadow and entrance travel need, so
    /// the window has to be placed that much nearer the screen edge to seat the card on
    /// the intended gap.
    /// </summary>
    public readonly record struct FlyoutGutter(double Left, double Top, double Right, double Bottom);

    /// <summary>Where a tray flyout is placed, and which way it travels as it opens.</summary>
    /// <param name="Left">Window left, in device-independent pixels.</param>
    /// <param name="Top">Window top, in device-independent pixels.</param>
    /// <param name="EnterX">Unit X of the direction the card enters from.</param>
    /// <param name="EnterY">Unit Y of the direction the card enters from.</param>
    /// <param name="CardAtTop">
    /// True when the card is aligned to the top of its window rather than the bottom.
    /// The window is taller than the card, so this decides which of its edges the card
    /// is glued to, and therefore which way a detail page grows.
    /// </param>
    public readonly record struct FlyoutPlacement(
        double Left,
        double Top,
        double EnterX,
        double EnterY,
        bool CardAtTop);

    /// <summary>
    /// Seats the Quick Panel against whichever corner the notification area is in.
    /// </summary>
    /// <remarks>
    /// Windows will dock the taskbar to any of the four screen edges, and the tray moves
    /// with it: bottom-right for a bar along the bottom, top-right along the top, and the
    /// far end of a vertical bar - bottom-left or bottom-right - when it is docked to a
    /// side. A flyout pinned to the bottom-right corner unconditionally opens at the
    /// wrong end of the screen on three of those four layouts, and rises out of an edge
    /// it is not attached to.
    ///
    /// <para>The arithmetic lives here, away from the window, because it is the part that
    /// decides whether the panel appears where the user pressed - and it can be checked
    /// for every taskbar edge without a second monitor, a DPI change or a restarted
    /// shell. <c>Arsenal.Tests</c> does.</para>
    /// </remarks>
    public static class TrayFlyout
    {
        /// <summary>
        /// Reserved space below this is not a docked bar. An auto-hidden taskbar keeps a
        /// sliver of the work area at most, and a stray pixel of rounding must not be
        /// read as a taskbar on that side.
        /// </summary>
        public const int MinimumReservedPixels = 8;

        /// <summary>
        /// Infers the docked edge from the space a monitor gives up out of its work area,
        /// which is the one signal that is per-monitor and therefore right for a
        /// secondary taskbar too.
        /// </summary>
        /// <param name="fallback">
        /// Used when nothing meaningful is reserved - an auto-hidden bar, or a monitor
        /// with no taskbar of its own. Windows docks every secondary taskbar to the same
        /// edge as the primary, so the primary bar's edge is the right answer there.
        /// </param>
        public static TaskbarEdge EdgeFromReservedSpace(
            Rectangle monitor, Rectangle workArea, TaskbarEdge fallback)
        {
            if (monitor.Width <= 0 || monitor.Height <= 0) return fallback;

            int left = workArea.Left - monitor.Left;
            int top = workArea.Top - monitor.Top;
            int right = monitor.Right - workArea.Right;
            int bottom = monitor.Bottom - workArea.Bottom;

            int widest = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
            if (widest < MinimumReservedPixels) return fallback;

            // A second docked app bar can reserve space on another side, so the widest
            // band wins rather than the first one found. Ties go to the edge tested
            // first, in the order Windows itself makes most likely.
            if (bottom == widest) return TaskbarEdge.Bottom;
            if (top == widest) return TaskbarEdge.Top;
            if (left == widest) return TaskbarEdge.Left;
            return TaskbarEdge.Right;
        }

        /// <summary>True when the notification area is at the left end of the screen.</summary>
        public static bool TrayIsOnTheLeft(TaskbarEdge edge) => edge == TaskbarEdge.Left;

        /// <summary>True when the notification area sits along the top of the screen.</summary>
        public static bool TrayIsAtTheTop(TaskbarEdge edge) => edge == TaskbarEdge.Top;

        /// <summary>
        /// Places the window so its card lands in the tray's corner with an equal gap off
        /// both edges it is seated against, and reports the direction it should travel.
        /// </summary>
        /// <param name="edge">The edge the taskbar is docked to.</param>
        /// <param name="workArea">The monitor work area, in device-independent pixels.</param>
        /// <param name="width">Window width, gutters included.</param>
        /// <param name="height">Window height, gutters included.</param>
        /// <param name="gap">The gap the card keeps off the work area, per side.</param>
        /// <param name="gutter">Transparent slack between the window and the card.</param>
        public static FlyoutPlacement Place(
            TaskbarEdge edge,
            FlyoutBounds workArea,
            double width,
            double height,
            double gap,
            FlyoutGutter gutter)
        {
            bool leftCorner = TrayIsOnTheLeft(edge);
            bool topCorner = TrayIsAtTheTop(edge);

            double left = leftCorner
                ? workArea.Left + Math.Max(0, gap - gutter.Left)
                : workArea.Right - width - Math.Max(0, gap - gutter.Right);

            double top = topCorner
                ? workArea.Top + Math.Max(0, gap - gutter.Top)
                : workArea.Bottom - height - Math.Max(0, gap - gutter.Bottom);

            // A window larger than the work area has no corner to sit in; keep its origin
            // on screen rather than pushing its far edge off one.
            left = Clamp(left, workArea.Left, workArea.Right - width);
            top = Clamp(top, workArea.Top, workArea.Bottom - height);

            // The card enters from behind the taskbar and travels inward, so the vector
            // points at the docked edge. A vertical bar therefore gets a horizontal
            // entrance, which is what makes the motion read as coming out of the bar
            // rather than off the bottom of an unrelated screen edge.
            (double enterX, double enterY) = edge switch
            {
                TaskbarEdge.Top => (0d, -1d),
                TaskbarEdge.Left => (-1d, 0d),
                TaskbarEdge.Right => (1d, 0d),
                _ => (0d, 1d)
            };

            return new FlyoutPlacement(left, top, enterX, enterY, topCorner);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            // Order matters when the window is larger than the work area: the minimum has
            // to win, or the origin is dragged off the near edge instead of the far one.
            if (value > maximum) value = maximum;
            return value < minimum ? minimum : value;
        }
    }
}
