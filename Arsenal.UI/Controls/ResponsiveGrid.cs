using System.Windows;
using System.Windows.Controls.Primitives;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// A <see cref="UniformGrid"/> that picks its column count from the width it is
    /// actually given, so dashboard tiles reflow instead of being crushed when the
    /// window is narrow or the navigation pane is open.
    /// </summary>
    public class ResponsiveGrid : UniformGrid
    {
        public static readonly DependencyProperty MinItemWidthProperty =
            DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(ResponsiveGrid),
                new FrameworkPropertyMetadata(220d, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Narrowest a tile may get before the grid drops a column.</summary>
        public double MinItemWidth
        {
            get => (double)GetValue(MinItemWidthProperty);
            set => SetValue(MinItemWidthProperty, value);
        }

        public static readonly DependencyProperty MaxColumnsProperty =
            DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid),
                new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Upper bound so tiles do not stretch into a single thin strip.</summary>
        public int MaxColumns
        {
            get => (int)GetValue(MaxColumnsProperty);
            set => SetValue(MaxColumnsProperty, value);
        }

        protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
        {
            double available = constraint.Width;
            int cap = Math.Max(1, MaxColumns);
            int desired = cap;

            if (!double.IsInfinity(available) && !double.IsNaN(available) && MinItemWidth > 0)
            {
                desired = Math.Clamp((int)Math.Floor(available / MinItemWidth), 1, cap);
            }

            // Columns affects measure, so only write it when it actually changed. The
            // count converges on the first pass, leaving at most one extra layout.
            if (Columns != desired) Columns = desired;

            return base.MeasureOverride(constraint);
        }
    }
}
