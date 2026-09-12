namespace Arsenal.UI.Controls
{
    /// <summary>
    /// Decides when the navigation column should fold itself away as the window narrows,
    /// and when it should come back.
    /// </summary>
    /// <remarks>
    /// Pulled out of the window so the rule can be exercised without one. It holds no
    /// WPF types and does no animating - the NavigationView animates its own pane width
    /// when <c>IsPaneOpen</c> changes, and the window grounds already follow that width
    /// frame by frame, so deciding <em>whether</em> is the whole of this job.
    /// </remarks>
    public sealed class ResponsivePaneState
    {
        /// <summary>
        /// How close to the window's minimum width the pane folds away, and how far past
        /// that it has to come back before the pane returns.
        /// </summary>
        /// <remarks>
        /// Two thresholds rather than one edge. Opening the pane is itself a layout
        /// change, so a single boundary would leave the pane opening and closing around
        /// it; the gap between these is wide enough that a drag settles on one side.
        /// </remarks>
        public const double CollapseMargin = 60;
        public const double ExpandMargin = 160;

        private bool _narrow;
        private bool _openBeforeNarrow = true;

        /// <summary>Whether the window is currently inside the narrow band.</summary>
        public bool IsNarrow => _narrow;

        /// <summary>
        /// Records what the user last asked for by hand, which is what the pane returns
        /// to once there is room for it again.
        /// </summary>
        public void UserSetPaneOpen(bool isPaneOpen) => _openBeforeNarrow = isPaneOpen;

        /// <summary>
        /// The state the pane should now be in, or null to leave it alone.
        /// </summary>
        /// <remarks>
        /// Edge-triggered: it answers once on entering the narrow band and once on
        /// leaving it, and says nothing in between. That is what keeps the toggle button
        /// working normally while the window is small - a user who opens the pane there
        /// keeps it open until the window crosses a threshold again.
        /// </remarks>
        public bool? Evaluate(double width, double minWidth, bool isPaneOpen)
        {
            if (width <= 0 || double.IsNaN(width)) return null;

            if (!_narrow && width <= minWidth + CollapseMargin)
            {
                _narrow = true;
                _openBeforeNarrow = isPaneOpen;
                return isPaneOpen ? false : null;
            }

            if (_narrow && width >= minWidth + ExpandMargin)
            {
                _narrow = false;
                return _openBeforeNarrow && !isPaneOpen ? true : null;
            }

            return null;
        }
    }
}
