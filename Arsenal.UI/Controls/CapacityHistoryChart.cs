using Arsenal.Battery;
using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using SystemFonts = System.Windows.SystemFonts;
using FlowDirection = System.Windows.FlowDirection;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// The battery's recorded capacity, week by week, as one line.
    /// </summary>
    /// <remarks>
    /// Drawn rather than charted. A chart library would bring axes, legends, tooltips
    /// and a theme of its own for a single unlabelled series of at most a few dozen
    /// points, and the thing worth seeing here is the shape of the line, not the
    /// readings: whether the battery is holding steady or sliding, and how fast.
    ///
    /// <para>The vertical scale is a share of design capacity, not of the highest
    /// reading. Scaled to its own data a two percent drop fills the box and looks
    /// alarming; against what the battery was built to hold, the same drop looks like
    /// what it is. The floor is 50% rather than zero, since a pack below half its
    /// design capacity is long past the point this chart is for.</para>
    /// </remarks>
    public class CapacityHistoryChart : FrameworkElement
    {
        private const double Floor = 0.5;
        private const double LabelHeight = 16;
        private static readonly Typeface StaticLabelTypeface = new(
            SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public static readonly DependencyProperty PointsProperty =
            DependencyProperty.Register(nameof(Points), typeof(IEnumerable), typeof(CapacityHistoryChart),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

        public IEnumerable? Points
        {
            get => (IEnumerable?)GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        public CapacityHistoryChart()
        {
            MinHeight = 120;
            ThemeRepaint.Follow(this);
        }

        private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var chart = (CapacityHistoryChart)d;

            // The collection is filled after it is bound, so a redraw has to follow the
            // items rather than only the property that holds them.
            if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= chart.OnItemsChanged;
            if (e.NewValue is INotifyCollectionChanged fresh) fresh.CollectionChanged += chart.OnItemsChanged;
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext context)
        {
            base.OnRender(context);

            var readings = new List<BatteryHistoryPoint>();
            foreach (object? item in Points ?? Array.Empty<object>())
                if (item is BatteryHistoryPoint point) readings.Add(point);

            if (readings.Count < 2 || ActualWidth <= 1 || ActualHeight <= LabelHeight + 2) return;

            var line = ThemeBrush("AccentPrimary", Colors.Teal);
            var grid = ThemeBrush("StrokeSubtle", Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
            var ink = ThemeBrush("TextTertiary", Colors.Gray);

            double plotHeight = ActualHeight - LabelHeight;
            var pen = new Pen(line, 2) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var gridPen = new Pen(grid, 1);
            gridPen.Freeze();

            // Guides at each tenth of design capacity, so the eye has something to
            // measure the slope against without an axis being drawn.
            for (double share = Floor; share <= 1.0001; share += 0.1)
            {
                double y = Y(share, plotHeight);
                context.DrawLine(gridPen, Snap(0, y), Snap(ActualWidth, y));
            }

            var geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                for (int index = 0; index < readings.Count; index++)
                {
                    var at = new Point(X(index, readings.Count), Y(readings[index].Retained, plotHeight));
                    if (index == 0) path.BeginFigure(at, false, false);
                    else path.LineTo(at, true, true);
                }
            }
            geometry.Freeze();

            // The area under the line, so a short series still reads as a quantity
            // rather than as two dots joined up.
            var fill = new StreamGeometry();
            using (StreamGeometryContext path = fill.Open())
            {
                path.BeginFigure(new Point(X(0, readings.Count), plotHeight), true, true);
                for (int index = 0; index < readings.Count; index++)
                    path.LineTo(new Point(X(index, readings.Count), Y(readings[index].Retained, plotHeight)), false, false);
                path.LineTo(new Point(X(readings.Count - 1, readings.Count), plotHeight), false, false);
            }
            fill.Freeze();

            var fillBrush = new SolidColorBrush(((SolidColorBrush)line).Color) { Opacity = 0.14 };
            fillBrush.Freeze();
            context.DrawGeometry(fillBrush, null, fill);
            context.DrawGeometry(null, pen, geometry);

            // Only the ends are labelled. A reading every week would crowd into an
            // unreadable band, and the question this answers is where it started and
            // where it has got to.
            Label(context, readings[0].Period, ink, 0, ActualWidth, false);
            Label(context, readings[^1].Period, ink, 0, ActualWidth, true);
        }

        private double X(int index, int count) => count <= 1 ? 0 : index * (ActualWidth - 2) / (count - 1) + 1;

        private static double Y(double share, double plotHeight)
        {
            double scaled = (Math.Clamp(share, Floor, 1) - Floor) / (1 - Floor);
            return plotHeight - scaled * (plotHeight - 2) - 1;
        }

        private static Point Snap(double x, double y) => new(x, Math.Round(y) + 0.5);

        private void Label(DrawingContext context, DateTime when, Brush ink, double left, double right, bool atEnd)
        {
            var text = new FormattedText(
                when.ToString("MMM yyyy", CultureInfo.CurrentCulture),
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                StaticLabelTypeface,
                11,
                ink,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double x = atEnd ? right - text.Width : left;
            context.DrawText(text, new Point(x, ActualHeight - LabelHeight + 2));
        }

        /// <summary>
        /// A theme brush, or a stand-in when the dictionaries are not reachable - a
        /// designer surface, or a control built before the app's resources are loaded.
        /// </summary>
        private Brush ThemeBrush(string key, Color fallback)
            => TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(fallback);
    }
}
