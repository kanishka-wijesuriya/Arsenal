using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// The scale drawn under a <see cref="ValueSlider"/>: a tick and a label at each
    /// marked value, so the range a control accepts is readable without dragging it.
    ///
    /// End labels are pulled inside the bounds rather than centred on their tick, which
    /// keeps the lowest and highest values flush with the ends of the track.
    /// </summary>
    public class SliderScale : FrameworkElement
    {
        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SliderScale),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SliderScale),
                new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public static readonly DependencyProperty MarksProperty =
            DependencyProperty.Register(nameof(Marks), typeof(DoubleCollection), typeof(SliderScale),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// Values to label. When null the scale shows just the minimum and maximum,
        /// which is what most sliders need.
        /// </summary>
        public DoubleCollection? Marks
        {
            get => (DoubleCollection?)GetValue(MarksProperty);
            set => SetValue(MarksProperty, value);
        }

        public static readonly DependencyProperty FormatProperty =
            DependencyProperty.Register(nameof(Format), typeof(string), typeof(SliderScale),
                new FrameworkPropertyMetadata("{0}", FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Same composite format the slider's readout uses.</summary>
        public string Format
        {
            get => (string)GetValue(FormatProperty);
            set => SetValue(FormatProperty, value);
        }

        public static readonly DependencyProperty TrackInsetProperty =
            DependencyProperty.Register(nameof(TrackInset), typeof(double), typeof(SliderScale),
                new FrameworkPropertyMetadata(9d, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// Half the slider thumb's width. The usable track starts and ends this far
        /// inside the control, so the scale has to match or the ends drift apart.
        /// </summary>
        public double TrackInset
        {
            get => (double)GetValue(TrackInsetProperty);
            set => SetValue(TrackInsetProperty, value);
        }

        private const double TickHeight = 4;
        private const double LabelGap = 3;
        private const double FontSize = 10;

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
            => new(0, TickHeight + LabelGap + FontSize + 4);

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            if (width <= 0 || Maximum <= Minimum) return;

            var values = Marks is { Count: > 0 } ? Marks.ToList() : new List<double> { Minimum, Maximum };

            Brush labelBrush = TryFindResource("TextTertiary") as Brush ?? Brushes.Gray;
            Brush tickBrush = TryFindResource("StrokeSubtle") as Brush ?? Brushes.Gray;
            var tickPen = new Pen(tickBrush, 1);
            tickPen.Freeze();

            var typeface = new Typeface("Segoe UI Variable Text, Segoe UI");
            double trackWidth = Math.Max(1, width - (TrackInset * 2));

            for (int i = 0; i < values.Count; i++)
            {
                double value = values[i];
                if (value < Minimum || value > Maximum) continue;

                double x = TrackInset + ((value - Minimum) / (Maximum - Minimum) * trackWidth);
                dc.DrawLine(tickPen, new Point(x, 0), new Point(x, TickHeight));

                var text = new FormattedText(
                    FormatValue(value),
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    FontSize,
                    labelBrush,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                // Keep the outermost labels inside the control instead of hanging off it.
                double labelX = x - (text.Width / 2);
                if (i == 0) labelX = Math.Max(0, labelX);
                if (i == values.Count - 1) labelX = Math.Min(width - text.Width, labelX);

                dc.DrawText(text, new Point(labelX, TickHeight + LabelGap));
            }
        }

        private string FormatValue(double value)
        {
            string format = string.IsNullOrEmpty(Format) ? "{0}" : Format;
            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, Math.Round(value));
            }
            catch (FormatException)
            {
                return Math.Round(value).ToString(CultureInfo.CurrentCulture);
            }
        }
    }
}
