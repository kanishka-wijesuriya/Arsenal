using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// A fixed-column grid whose children slide to their new places instead of jumping.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="System.Windows.Controls.Primitives.UniformGrid"/> re-lays out
    /// instantly, which is fine when the order only changes on a button press but reads
    /// as a flicker when tiles are being dragged past each other. Every child keeps a
    /// translate transform; when a child is arranged somewhere new it is offset back to
    /// where it was and animated to zero, so the reflow is visible as movement.
    ///
    /// The tile being dragged is excluded: its transform is the pointer's, and animating
    /// it would fight the drag.
    /// </remarks>
    public class AnimatedTileGrid : System.Windows.Controls.Panel
    {
        private const int ReflowMs = 170;

        private readonly Dictionary<UIElement, Rect> _placed = new();

        public static readonly DependencyProperty ColumnsProperty =
            DependencyProperty.Register(nameof(Columns), typeof(int), typeof(AnimatedTileGrid),
                new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public int Columns
        {
            get => (int)GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        /// <summary>
        /// The child under the pointer during a drag. Left where the drag put it.
        /// </summary>
        public UIElement? DraggingChild { get; set; }

        /// <summary>Height of one row, once measured. Used to size a page of tiles.</summary>
        public double RowHeight { get; private set; }

        public int RowCount => (InternalChildren.Count + Math.Max(1, Columns) - 1) / Math.Max(1, Columns);

        /// <summary>
        /// Where the child was last arranged, or null before it has been. A dragged tile
        /// is drawn at this point plus its transform, so a drag that wants to stay under
        /// the pointer has to know where the cell underneath it has moved to.
        /// </summary>
        public Point? CellOrigin(UIElement child) =>
            _placed.TryGetValue(child, out Rect rect) ? rect.Location : null;

        /// <summary>The transform a child is positioned by, created on first use.</summary>
        public static TranslateTransform OffsetOf(UIElement child)
        {
            if (child.RenderTransform is TranslateTransform existing) return existing;
            var transform = new TranslateTransform();
            child.RenderTransform = transform;
            return transform;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            int columns = Math.Max(1, Columns);
            double cellWidth = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : availableSize.Width / columns;

            double rowHeight = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(cellWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            RowHeight = rowHeight;
            double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            return new Size(width, rowHeight * RowCount);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int columns = Math.Max(1, Columns);
            double cellWidth = finalSize.Width / columns;

            // The measured row height, never finalSize divided by the row count. This
            // panel is deliberately taller than the viewport that clips it - that overflow
            // is what the second page of tiles is - and WPF clamps a child's DesiredSize
            // to the space it was offered, so dividing by the row count squashed every row
            // to fit a page and left the later ones stacked invisibly on top of the first.
            double cellHeight = RowHeight > 0 ? RowHeight : finalSize.Height / Math.Max(1, RowCount);

            var live = new HashSet<UIElement>();
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                UIElement child = InternalChildren[i];
                live.Add(child);

                var cell = new Rect(i % columns * cellWidth, i / columns * cellHeight, cellWidth, cellHeight);
                child.Arrange(cell);
                SlideInto(child, cell);
            }

            // Containers are recycled as the collection changes; a stale entry would make
            // a reused container animate in from wherever its predecessor sat.
            foreach (UIElement gone in _placed.Keys.Where(key => !live.Contains(key)).ToList())
                _placed.Remove(gone);

            return finalSize;
        }

        private void SlideInto(UIElement child, Rect cell)
        {
            bool moved = _placed.TryGetValue(child, out Rect previous);
            _placed[child] = cell;

            if (ReferenceEquals(child, DraggingChild)) return;

            TranslateTransform offset = OffsetOf(child);

            (FrameEase easeX, FrameEase easeY) = ReflowEasesFor(offset);

            // First arrangement: nothing to slide from, so land in place.
            if (!moved || (Math.Abs(previous.X - cell.X) < 0.5 && Math.Abs(previous.Y - cell.Y) < 0.5))
            {
                easeX.Stop();
                easeY.Stop();
                offset.X = 0;
                offset.Y = 0;
                return;
            }

            easeX.Start(previous.X - cell.X, 0, ReflowMs, FrameEase.CubicOut, v => offset.X = v);
            easeY.Start(previous.Y - cell.Y, 0, ReflowMs, FrameEase.CubicOut, v => offset.Y = v);
        }

        /// <summary>
        /// One pair of eases per tile transform, kept alive only as long as the transform
        /// itself. A reflow moves every tile at once, so each needs its own clock.
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            TranslateTransform, ReflowEases> ReflowTable = new();

        private sealed class ReflowEases
        {
            public readonly FrameEase X = new();
            public readonly FrameEase Y = new();
        }

        private static (FrameEase X, FrameEase Y) ReflowEasesFor(TranslateTransform offset)
        {
            ReflowEases pair = ReflowTable.GetValue(offset, static _ => new ReflowEases());
            return (pair.X, pair.Y);
        }
    }
}
