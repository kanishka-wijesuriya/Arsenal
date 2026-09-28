using Arsenal.UI.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using TranslateTransform = System.Windows.Media.TranslateTransform;

namespace Arsenal.UI.Views.Windows
{
    public partial class QuickPanelWindow
    {

        /// <summary>
        /// The items panel, which is only reachable through the visual tree - an
        /// ItemsPanelTemplate's name is scoped to the template, not to this window.
        /// </summary>
        private Controls.AnimatedTileGrid? TileGrid => FindGrid(QuickTilesGrid);

        private static Controls.AnimatedTileGrid? FindGrid(DependencyObject root)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Controls.AnimatedTileGrid grid) return grid;
                if (FindGrid(child) is { } nested) return nested;
            }
            return null;
        }

        /// <summary>Height of one page of tiles, or 0 before the grid has measured.</summary>
        private double TilePageHeight { get; set; }
        private int _tilePageWheelDirection;

        /// <summary>
        /// Wheel travel banked toward the next page change, in wheel-delta units.
        /// </summary>
        /// <remarks>
        /// This used to be a single "gesture consumed" flag, which spent one page per
        /// gesture and then discarded every packet until the wheel had been still for the
        /// idle interval. Holding a spin therefore moved exactly one page and then
        /// appeared to stop responding. Banking the travel instead means a wheel notch is
        /// always worth a page, and a touchpad swipe is worth however many notches of
        /// travel it actually carried.
        /// </remarks>
        private double _tilePageWheelTravel;

        /// <summary>
        /// Earliest tick at which the next page change may start, so a fast spin cannot
        /// outrun the slide and turn several pages into one blur.
        /// </summary>
        private long _tilePageWheelNextAllowedAt;

        private static readonly long TilePageWheelSpacingTicks =
            TimeSpan.FromMilliseconds(130).Ticks;

        /// <summary>
        /// Clips the tile viewport to a page. Tiles past the first page sit below the
        /// clip and are reached by sliding the whole list, so the panel keeps its height
        /// however many tiles are on it.
        /// </summary>
        /// <summary>Guards the mutual call with <see cref="SlideToTilePage(bool)"/>.</summary>
        private bool _fittingTilePages;

        private void FitTilePageViewport()
        {
            if (_fittingTilePages || TileGrid is not { } grid) return;

            _fittingTilePages = true;
            try
            {
                QuickTilesGrid.UpdateLayout();
                double rowHeight = grid.RowHeight;
                if (rowHeight <= 0) return;

                int rowsPerPage = QuickPanelViewModel.TilesPerPage / Math.Max(1, grid.Columns);
                int rows = Math.Min(rowsPerPage, Math.Max(1, grid.RowCount));

                TilePageHeight = rowHeight * rowsPerPage;
                TilePageViewport.Height = rowHeight * rows;
            }
            finally
            {
                _fittingTilePages = false;
            }

            SlideToTilePage(animate: false);
        }

        private void SlideToTilePage() => SlideToTilePage(animate: true);

        private void TilePageWheelIdleTimer_Tick(object? sender, EventArgs e)
        {
            _tilePageWheelIdleTimer.Stop();
            // Travel left over from a gesture that stopped short of a page is not carried
            // into the next one, so a page never turns from a flick the user has finished.
            _tilePageWheelTravel = 0;
            _tilePageWheelDirection = 0;
            _tilePageWheelNextAllowedAt = 0;
        }

        /// <summary>
        /// Banks this packet's travel and reports whether it has bought a page change.
        /// </summary>
        private bool TryBeginTilePageWheel(int delta)
        {
            if (delta == 0)
                return false;

            int direction = Math.Sign(delta);
            if (direction != _tilePageWheelDirection)
            {
                // A reversal is a new gesture; whatever was banked pointed the other way.
                _tilePageWheelTravel = 0;
                _tilePageWheelDirection = direction;
                _tilePageWheelNextAllowedAt = 0;
            }

            _tilePageWheelIdleTimer.Stop();
            _tilePageWheelIdleTimer.Start();

            // Capped so a spin faster than the rate limit below cannot build a backlog of
            // pages that would keep turning after the wheel has stopped.
            _tilePageWheelTravel = Math.Min(
                _tilePageWheelTravel + Math.Abs(delta),
                WheelDeltaPerNotch * 2d);

            if (_tilePageWheelTravel < WheelDeltaPerNotch)
                return false;

            // A spin faster than the slide can present would just blur pages together, so
            // hold the extra travel until the previous change has had time to be seen.
            long now = DateTime.UtcNow.Ticks;
            if (now < _tilePageWheelNextAllowedAt)
                return false;

            _tilePageWheelTravel -= WheelDeltaPerNotch;
            _tilePageWheelNextAllowedAt = now + TilePageWheelSpacingTicks;
            return true;
        }

        private const double WheelDeltaPerNotch = 120d;

        private void TilePageViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is not QuickPanelViewModel vm || !vm.HasTilePages || _dragActive)
                return;

            // A precision touchpad emits several small wheel packets for one gesture.
            // The idle timer re-arms independently after the stream finishes, instead
            // of making the next input packet both re-arm and get discarded. A genuine
            // direction reversal is accepted immediately even before that timer fires.
            e.Handled = true;
            if (!TryBeginTilePageWheel(e.Delta))
                return;

            if (e.Delta < 0 && vm.CanGoForwardTilePage)
            {
                vm.NextTilePage();
            }
            else if (e.Delta > 0 && vm.CanGoBackTilePage)
            {
                vm.PreviousTilePage();
            }
        }

        private void SlideToTilePage(bool animate)
        {
            if (DataContext is not QuickPanelViewModel vm) return;

            // A page change can be the first thing that ever needs the measurement - the
            // arrows are reachable before anything else has had cause to take it.
            if (TilePageHeight <= 0) FitTilePageViewport();
            if (TilePageHeight <= 0) return;

            double target = -vm.TilePage * TilePageHeight;

            // Held, not restarted from zero: pressing the arrow twice quickly should carry
            // on from wherever the first slide had reached.
            double current = TilePageShift.Y;
            _tilePageSlide.Stop();
            TilePageShift.Y = current;

            // A dragged tile must remain attached to the pointer. Moving the backing
            // page while pointer coordinates already refer to the destination page
            // makes cross-page drops jump and land in the wrong slot.
            if (!animate || _dragActive || Math.Abs(current - target) < 0.5)
            {
                TilePageShift.Y = target;
                return;
            }

            // The cache is normally warmed before input reaches this method. The guard is
            // for a page command fired unusually early during first layout.
            SetTileBitmapCache(true);

            // Driven per presented frame rather than by a WPF timeline. The timeline moved
            // this transform only a handful of times across the whole 320ms - the slide
            // arrived as two or three jumps - whatever DesiredFrameRate it was given. See
            // FrameEase for the measurements.
            _tilePageSlide.Start(
                current,
                target,
                320d,
                Controls.FrameEase.SineInOut,
                y => TilePageShift.Y = y);
        }

        private readonly Controls.FrameEase _tilePageSlide = new();
        private readonly Controls.FrameEase _tileSettleX = new();
        private readonly Controls.FrameEase _tileSettleY = new();

        /// <summary>
        /// Keeps the complete two-page tile strip as one retained compositor surface.
        /// Creating it at page-switch time caused a large first-frame rasterisation, and
        /// removing it at completion caused a second visible hitch. It is disabled only
        /// while a tile is following the pointer, when its contents genuinely change on
        /// every input frame.
        /// </summary>
        /// <summary>
        /// Gives back what the panel only needs while it is on screen.
        /// </summary>
        /// <remarks>
        /// This window is a singleton: once opened it is hidden rather than destroyed, so
        /// everything it holds stays held for the rest of the session. Two things are
        /// worth releasing. The tile grid carries a <see cref="System.Windows.Media.BitmapCache"/>,
        /// which is a rasterised copy of the whole grid at screen resolution and is of no
        /// use to a hidden window. And nothing on this path ever collected: the main
        /// window trims when it returns to the tray, the panel did not, which is why
        /// closing the panel released nothing until the main window happened to be opened
        /// and closed afterwards.
        ///
        /// The trim waits for the close animation. It induces a gen2 collection, and doing
        /// that while the last frames are still being composited on a layered window is
        /// exactly where a stutter would show.
        /// </remarks>
        private void OnPanelVisibilityChanged(bool visible)
        {
            if (visible)
                Services.BackgroundMemoryRelease.NotifyActivity();
            else
                Services.BackgroundMemoryRelease.Schedule();
        }

        private void SetTileBitmapCache(bool cached)
        {
            // Retained as a no-op method so call sites remain clean.
            // Hardware acceleration renders the vector tiles directly without the
            // 27MB offscreen DirectX bitmap texture and working set spike.
        }

        private Controls.QuickTile? _draggingTile;
        private QuickTileSlot? _draggingSlot;
        private System.Windows.Point _dragOrigin;

        /// <summary>Where in the tile the pointer took hold of it.</summary>
        private System.Windows.Point _grabWithinTile;
        private bool _dragActive;
        private long _lastDragPageSwitchAt;
        private long _dragPageEdgeSince;
        private int _dragPageEdgeDirection;

        /// <summary>
        /// Changes page when a dragged tile reaches the top or bottom edge. Mouse
        /// capture keeps these events arriving just outside the clipped viewport too.
        /// </summary>
        private bool TrySwitchTilePageDuringDrag(System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragActive || DataContext is not QuickPanelViewModel vm || !vm.HasTilePages)
                return false;

            long now = Environment.TickCount64;
            System.Windows.Point viewportPoint = e.GetPosition(TilePageViewport);
            const double edge = 24;
            int direction = viewportPoint.Y <= edge ? -1
                : viewportPoint.Y >= TilePageViewport.ActualHeight - edge ? 1
                : 0;

            if (direction == 0)
            {
                _dragPageEdgeDirection = 0;
                _dragPageEdgeSince = 0;
                return false;
            }
            if (direction != _dragPageEdgeDirection)
            {
                _dragPageEdgeDirection = direction;
                _dragPageEdgeSince = now;
                return false;
            }
            if (now - _dragPageEdgeSince < 180 || now - _lastDragPageSwitchAt < 420)
                return false;

            int before = vm.TilePage;

            if (direction < 0 && vm.CanGoBackTilePage)
                vm.PreviousTilePage();
            else if (direction > 0 && vm.CanGoForwardTilePage)
                vm.NextTilePage();

            if (vm.TilePage == before) return false;

            _lastDragPageSwitchAt = now;
            _dragPageEdgeSince = now;
            QuickTilesGrid.UpdateLayout();
            return true;
        }

        /// <summary>
        /// Reordering by dragging, done with mouse capture rather than
        /// <see cref="DragDrop.DoDragDrop"/>: that runs its own modal loop and takes the
        /// foreground with it, which this window reads as being clicked away from and
        /// answers by closing itself mid-drag.
        /// </summary>
        private void HookTileDragging()
        {
            QuickTilesGrid.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (DataContext is not QuickPanelViewModel { IsEditingTiles: true }) return;
                if (FindTile(e.OriginalSource as DependencyObject) is not { } tile) return;
                if (tile.DataContext is not QuickTileSlot slot) return;

                _draggingTile = tile;
                _draggingSlot = slot;
                _dragOrigin = e.GetPosition(QuickTilesGrid);
                _dragActive = false;
            };

            QuickTilesGrid.PreviewMouseMove += (_, e) =>
            {
                if (_draggingTile is null || e.LeftButton != MouseButtonState.Pressed) return;

                System.Windows.Point here = e.GetPosition(QuickTilesGrid);
                if (!_dragActive)
                {
                    if (Math.Abs(here.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
                        && Math.Abs(here.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

                    _dragActive = true;
                    _lastDragPageSwitchAt = 0;
                    _dragPageEdgeSince = 0;
                    _dragPageEdgeDirection = 0;
                    SetTileBitmapCache(false);
                    _draggingTile.CaptureMouse();
                    _draggingTile.Opacity = 0.9;

                    // On the panel's own child, not on the tile inside it: ZIndex is read
                    // off the direct children of the panel that arranges them, so setting
                    // it on the tile did nothing and the one being dragged slid underneath
                    // every tile it passed over.
                    if (ContainerOf(_draggingTile) is { } lifted)
                    {
                        System.Windows.Controls.Panel.SetZIndex(lifted, 10);
                        if (TileGrid is { } starting)
                        {
                            starting.DraggingChild = lifted;

                            // Where inside the tile it was picked up. Everything after this
                            // is measured from the cell the tile currently occupies, so the
                            // grab point survives the cell changing underneath it.
                            System.Windows.Point cell = starting.CellOrigin(lifted) ?? default;
                            _grabWithinTile = new System.Windows.Point(
                                _dragOrigin.X - cell.X, _dragOrigin.Y - cell.Y);
                        }
                    }
                }

                if (TrySwitchTilePageDuringDrag(e))
                    here = e.GetPosition(QuickTilesGrid);

                FollowPointer(here);
                ReorderUnderPointer(e);
            };

            QuickTilesGrid.PreviewMouseLeftButtonUp += (_, _) => EndTileDrag();
            QuickTilesGrid.MouseLeave += (_, e) => { if (e.LeftButton != MouseButtonState.Pressed) EndTileDrag(); };
        }

        /// <summary>
        /// Puts the dragged tile back under the pointer.
        /// </summary>
        /// <remarks>
        /// The offset is measured from the cell the tile currently sits in rather than
        /// from where the drag began. Those are the same thing until the tile is reordered
        /// - and at that moment the cell moves out from under it, so an offset anchored to
        /// the starting point left the tile displaced by a whole cell and the pointer no
        /// longer on it.
        /// </remarks>
        private void FollowPointer(System.Windows.Point here)
        {
            if (TileGrid is not { } grid) return;
            if (ContainerOf(_draggingTile) is not { } container) return;

            System.Windows.Point cell = grid.CellOrigin(container) ?? default;
            TranslateTransform offset = Controls.AnimatedTileGrid.OffsetOf(container);
            offset.X = here.X - cell.X - _grabWithinTile.X;
            offset.Y = here.Y - cell.Y - _grabWithinTile.Y;
        }

        /// <summary>Moves the dragged tile into whichever slot the pointer is over.</summary>
        private void ReorderUnderPointer(System.Windows.Input.MouseEventArgs e)
        {
            if (DataContext is not QuickPanelViewModel vm || _draggingSlot is null) return;
            if (TileGrid is not { } grid || grid.RowHeight <= 0) return;

            // The pointer's position within the list, not the viewport: the list is
            // shifted up by whole pages, so a drag on page two is past that offset.
            System.Windows.Point point = e.GetPosition(QuickTilesGrid);
            int columns = Math.Max(1, grid.Columns);
            double columnWidth = QuickTilesGrid.ActualWidth / columns;
            if (columnWidth <= 0) return;

            int column = Math.Clamp((int)(point.X / columnWidth), 0, columns - 1);
            int row = Math.Max(0, (int)(point.Y / grid.RowHeight));
            int index = Math.Clamp(row * columns + column, 0, vm.Tiles.Count - 1);

            if (vm.Tiles.IndexOf(_draggingSlot) == index) return;

            vm.MoveTile(_draggingSlot, index);

            // Laid out at once, then re-pinned: the tile's cell has just changed, and
            // FollowPointer measures from that cell.
            if (ContainerOf(_draggingTile) is { } container) grid.DraggingChild = container;
            QuickTilesGrid.UpdateLayout();
            FollowPointer(point);
        }

        private void EndTileDrag()
        {
            if (_draggingTile is null) return;

            Controls.QuickTile tile = _draggingTile;
            bool wasDragging = _dragActive;
            _draggingTile = null;
            _dragActive = false;

            tile.ReleaseMouseCapture();
            tile.Opacity = 1;

            if (TileGrid is { } grid) grid.DraggingChild = null;

            if (ContainerOf(tile) is { } container)
            {
                TranslateTransform offset = Controls.AnimatedTileGrid.OffsetOf(container);
                _tileSettleX.Start(offset.X, 0, 170, Controls.FrameEase.CubicOut, v => offset.X = v);

                // Dropped back into the stack only once it has finished travelling, so it
                // does not disappear behind its neighbours part-way through settling.
                _tileSettleY.Start(
                    offset.Y, 0, 170, Controls.FrameEase.CubicOut,
                    v => offset.Y = v,
                    completed: () =>
                    {
                        System.Windows.Controls.Panel.SetZIndex(container, 0);
                        if (!_dragActive && _draggingTile is null && IsVisible) SetTileBitmapCache(true);
                    });
            }
            else if (wasDragging)
            {
                if (IsVisible) SetTileBitmapCache(true);
            }

            if (wasDragging && DataContext is QuickPanelViewModel vm) vm.CommitTileOrder();
            _draggingSlot = null;
        }

        /// <summary>The panel child holding a tile - what the grid arranges and animates.</summary>
        private UIElement? ContainerOf(Controls.QuickTile? tile)
        {
            if (tile is null || TileGrid is not { } grid) return null;

            DependencyObject? node = tile;
            while (node is not null)
            {
                DependencyObject? parent = System.Windows.Media.VisualTreeHelper.GetParent(node);
                if (ReferenceEquals(parent, grid)) return node as UIElement;
                node = parent;
            }
            return null;
        }

        private static Controls.QuickTile? FindTile(DependencyObject? source)
        {
            while (source is not null)
            {
                if (source is Controls.QuickTile tile) return tile;
                source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                    : System.Windows.LogicalTreeHelper.GetParent(source);
            }
            return null;
        }
    }
}
