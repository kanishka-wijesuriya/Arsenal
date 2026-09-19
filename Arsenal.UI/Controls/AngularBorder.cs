using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace Arsenal.UI.Controls
{
    /// <summary>Which corners of an <see cref="AngularBorder"/> are cut away.</summary>
    [Flags]
    public enum PanelCorners
    {
        None = 0,
        TopLeft = 1,
        TopRight = 2,
        BottomRight = 4,
        BottomLeft = 8,

        /// <summary>The usual pair: cut opposite corners so the panel reads as machined.</summary>
        Diagonal = TopRight | BottomLeft,
        All = TopLeft | TopRight | BottomRight | BottomLeft,
    }

    /// <summary>
    /// A panel with chamfered corners instead of rounded ones.
    /// </summary>
    /// <remarks>
    /// WPF's Border can round a corner but cannot cut one, and a cut corner is most of
    /// what separates a hardware-console panel from an ordinary card. Subclassing Border
    /// rather than writing a new control keeps Child, Padding, BorderThickness and the
    /// whole layout contract: only the painting changes, so this drops into a template
    /// anywhere a Border was.
    ///
    /// <para>The geometry is built once per size change and cached, because these are
    /// used for every row and card on a page and rebuilding a PathGeometry on each
    /// render pass would be the most expensive thing on screen.</para>
    /// </remarks>
    public class AngularBorder : Border
    {
        public static readonly DependencyProperty CutProperty =
            DependencyProperty.Register(nameof(Cut), typeof(double), typeof(AngularBorder),
                new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsArrange, OnGeometryChanged));

        /// <summary>How far each cut corner is taken back, in device-independent pixels.</summary>
        public double Cut
        {
            get => (double)GetValue(CutProperty);
            set => SetValue(CutProperty, value);
        }

        public static readonly DependencyProperty CornersProperty =
            DependencyProperty.Register(nameof(Corners), typeof(PanelCorners), typeof(AngularBorder),
                new FrameworkPropertyMetadata(PanelCorners.Diagonal, FrameworkPropertyMetadataOptions.AffectsArrange, OnGeometryChanged));

        /// <summary>Which corners are cut. The rest stay square.</summary>
        public PanelCorners Corners
        {
            get => (PanelCorners)GetValue(CornersProperty);
            set => SetValue(CornersProperty, value);
        }

        public static readonly DependencyProperty ClipsContentProperty =
            DependencyProperty.Register(nameof(ClipsContent), typeof(bool), typeof(AngularBorder),
                // Arrange, not Render: the clip is applied during arrange, so a change
                // to this would otherwise not take effect until something else moved.
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange, OnGeometryChanged));

        /// <summary>
        /// Clip the child to the cut outline. Off by default: clipping forces the whole
        /// subtree through an intermediate surface, which is not worth paying for on a
        /// panel whose content does not reach its corners.
        /// </summary>
        public bool ClipsContent
        {
            get => (bool)GetValue(ClipsContentProperty);
            set => SetValue(ClipsContentProperty, value);
        }

        private static void OnGeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((AngularBorder)d).InvalidateGeometry();

        private Geometry? _outline;
        private Geometry? _stroke;
        private Size _builtFor = Size.Empty;
        private double _builtWeight = double.NaN;

        private void InvalidateGeometry()
        {
            _outline = null;
            _stroke = null;
            _builtFor = Size.Empty;
            _builtWeight = double.NaN;
            InvalidateVisual();
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size arranged = base.ArrangeOverride(finalSize);

            // Measured from finalSize, never from ActualWidth. ActualWidth is only
            // written once arrange has returned, so on the pass that first sizes the
            // control it is still the previous value - zero, the first time. Reading it
            // here left the clip unset until some later pass re-arranged the control,
            // which is why a chip's lit edge hung outside its shape until it was
            // hovered.
            EnsureGeometry(finalSize);

            // Assigned here rather than in OnRender: Clip affects rendering, so setting
            // it mid-render schedules another render pass to apply it.
            Clip = ClipsContent ? _outline : null;
            return arranged;
        }

        protected override void OnRender(DrawingContext dc)
        {
            // Deliberately not calling base: Border would paint its own rounded
            // rectangle underneath this one.
            Size size = RenderSize;
            if (size.Width <= 0 || size.Height <= 0) return;

            EnsureGeometry(size);
            if (Background is not null) dc.DrawGeometry(Background, null, _outline);

            if (_stroke is not null && BorderBrush is not null)
            {
                var pen = new Pen(BorderBrush, BorderThickness.Left);
                pen.Freeze();
                dc.DrawGeometry(null, pen, _stroke);
            }
        }

        /// <summary>
        /// Rebuilds the outline and the stroke path if this size is new.
        /// </summary>
        /// <remarks>
        /// Cached because these are used for every row, chip and card on a page, and
        /// rebuilding two PathGeometries per render pass would be the most expensive
        /// thing on screen.
        /// </remarks>
        private void EnsureGeometry(Size size)
        {
            // Border weight is part of the key, not just the size. It is a theme value,
            // so switching themes changes it while the control keeps its size, and a
            // cache keyed on size alone would go on stroking the old weight's path.
            double weight = BorderThickness.Left;
            if (_outline is not null && _builtFor == size && _builtWeight.Equals(weight)) return;

            _builtFor = size;
            _builtWeight = weight;
            _outline = Build(0, size);

            // The stroke straddles the path it is drawn on, so the path is pulled in by
            // half the pen to keep the whole border inside the control's own bounds.
            _stroke = weight > 0 ? Build(weight / 2, size) : null;
        }

        /// <summary>
        /// The cut outline, optionally pulled in from the edges so a stroke drawn on it
        /// sits fully inside the control.
        /// </summary>
        private Geometry Build(double inset, Size size)
        {
            double left = inset;
            double top = inset;
            double right = Math.Max(left, size.Width - inset);
            double bottom = Math.Max(top, size.Height - inset);

            // A cut can never eat more than half the shorter side, or opposite cuts meet
            // and the panel collapses into a diamond on a short row.
            double cut = Math.Max(0, Math.Min(Cut, Math.Min(right - left, bottom - top) / 2));

            var figure = new PathFigure { IsClosed = true, IsFilled = true };
            figure.StartPoint = Has(PanelCorners.TopLeft)
                ? new Point(left + cut, top)
                : new Point(left, top);

            if (Has(PanelCorners.TopRight))
            {
                figure.Segments.Add(new LineSegment(new Point(right - cut, top), true));
                figure.Segments.Add(new LineSegment(new Point(right, top + cut), true));
            }
            else figure.Segments.Add(new LineSegment(new Point(right, top), true));

            if (Has(PanelCorners.BottomRight))
            {
                figure.Segments.Add(new LineSegment(new Point(right, bottom - cut), true));
                figure.Segments.Add(new LineSegment(new Point(right - cut, bottom), true));
            }
            else figure.Segments.Add(new LineSegment(new Point(right, bottom), true));

            if (Has(PanelCorners.BottomLeft))
            {
                figure.Segments.Add(new LineSegment(new Point(left + cut, bottom), true));
                figure.Segments.Add(new LineSegment(new Point(left, bottom - cut), true));
            }
            else figure.Segments.Add(new LineSegment(new Point(left, bottom), true));

            if (Has(PanelCorners.TopLeft))
                figure.Segments.Add(new LineSegment(new Point(left, top + cut), true));
            else figure.Segments.Add(new LineSegment(new Point(left, top), true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        private bool Has(PanelCorners corner) => (Corners & corner) == corner;
    }
}
