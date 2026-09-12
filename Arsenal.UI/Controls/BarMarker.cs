using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// A single upright tick drawn over a progress bar at a given percentage, marking
    /// the point the bar is not allowed to pass: the charge limit, in practice.
    /// Hidden when the position is zero or a full 100.
    /// </summary>
    public class BarMarker : FrameworkElement
    {
        public static readonly DependencyProperty PositionProperty =
            DependencyProperty.Register(nameof(Position), typeof(double), typeof(BarMarker),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Where to draw the tick, from 0 to 100.</summary>
        public double Position
        {
            get => (double)GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0) return;
            if (Position <= 0 || Position >= 100) return;

            Brush brush = TryFindResource("AccentPrimary") as Brush ?? Brushes.Gray;
            var pen = new Pen(brush, 2);
            pen.Freeze();

            double x = Math.Clamp(Position / 100d, 0, 1) * width;
            x = Math.Clamp(x, 1, width - 1);

            dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }
    }
}
