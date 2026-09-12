using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// A slider with its numeric readout, sized once so that every slider in the app
    /// has the same track length and the same right-aligned value column.
    ///
    /// The readout is derived from <see cref="Format"/>; pass <see cref="ValueText"/>
    /// when the view model already produces the string.
    /// </summary>
    public class ValueSlider : System.Windows.Controls.Control
    {

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(ValueSlider),
                new PropertyMetadata(0d, OnRangeChanged));

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(ValueSlider),
                new PropertyMetadata(100d, OnRangeChanged));

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(ValueSlider),
                new FrameworkPropertyMetadata(
                    0d,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnDisplayInputChanged,
                    CoerceValueToRange));

        /// <summary>Two-way by default, matching a plain <see cref="Slider"/>.</summary>
        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        /// <summary>
        /// Whether the range is settled enough to hold the value to it.
        /// </summary>
        /// <remarks>
        /// Bindings attach in the order they are written, so a slider whose bounds come
        /// from a view model has them before its value - but only once the data context
        /// has reached it. Coercing before that would measure the value against the
        /// registered defaults and write the result back through the two-way binding,
        /// which is the data loss this coercion exists to prevent. Loaded is the first
        /// moment every binding on the control has certainly run.
        /// </remarks>
        private bool _rangeSettled;

        /// <summary>
        /// Holds the readout to the track. The inner <see cref="Slider"/> clamps its own
        /// value anyway, so without this the two disagree: the thumb sits at the end of
        /// the track while the readout keeps printing a number the track cannot reach.
        /// </summary>
        private static object CoerceValueToRange(DependencyObject d, object baseValue)
        {
            var slider = (ValueSlider)d;
            if (!slider._rangeSettled) return baseValue;

            double value = (double)baseValue;
            double min = slider.Minimum;
            double max = slider.Maximum;

            // A collapsed or inverted range says nothing useful about the value; leaving
            // it alone beats snapping every such slider to a single point.
            if (max <= min) return baseValue;

            return value < min ? min : value > max ? max : value;
        }

        private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var slider = (ValueSlider)d;
            if (slider._rangeSettled) slider.CoerceValue(ValueProperty);
        }

        public static readonly DependencyProperty TickFrequencyProperty =
            DependencyProperty.Register(nameof(TickFrequency), typeof(double), typeof(ValueSlider),
                new PropertyMetadata(1d));

        public double TickFrequency
        {
            get => (double)GetValue(TickFrequencyProperty);
            set => SetValue(TickFrequencyProperty, value);
        }

        public static readonly DependencyProperty IsSnapToTickEnabledProperty =
            DependencyProperty.Register(nameof(IsSnapToTickEnabled), typeof(bool), typeof(ValueSlider),
                new PropertyMetadata(false));

        public bool IsSnapToTickEnabled
        {
            get => (bool)GetValue(IsSnapToTickEnabledProperty);
            set => SetValue(IsSnapToTickEnabledProperty, value);
        }

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(ValueSlider),
                new PropertyMetadata(null));

        /// <summary>
        /// Optional label placed in the same row as the track. Rendering it inside the
        /// control keeps it centred on the bar itself rather than on the bar plus the
        /// scale underneath, which is what an outside label would centre against.
        /// </summary>
        public string? Header
        {
            get => (string?)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public static readonly DependencyProperty HeaderWidthProperty =
            DependencyProperty.Register(nameof(HeaderWidth), typeof(double), typeof(ValueSlider),
                new PropertyMetadata(double.NaN));

        /// <summary>Fixed width for the header column so stacked sliders line up.</summary>
        public double HeaderWidth
        {
            get => (double)GetValue(HeaderWidthProperty);
            set => SetValue(HeaderWidthProperty, value);
        }

        public static readonly DependencyProperty ReadoutWidthProperty =
            DependencyProperty.Register(nameof(ReadoutWidth), typeof(double), typeof(ValueSlider),
                new PropertyMetadata(62d));

        /// <summary>
        /// Width reserved for the formatted value. Compact surfaces can reduce this
        /// without changing the shared full-window slider geometry.
        /// </summary>
        public double ReadoutWidth
        {
            get => (double)GetValue(ReadoutWidthProperty);
            set => SetValue(ReadoutWidthProperty, value);
        }

        public static readonly DependencyProperty ReadoutMarginProperty =
            DependencyProperty.Register(nameof(ReadoutMargin), typeof(Thickness), typeof(ValueSlider),
                new PropertyMetadata(new Thickness(12, 0, 0, 0)));

        /// <summary>Gap between the end of the track and its value readout.</summary>
        public Thickness ReadoutMargin
        {
            get => (Thickness)GetValue(ReadoutMarginProperty);
            set => SetValue(ReadoutMarginProperty, value);
        }

        public static readonly DependencyProperty TicksProperty =
            DependencyProperty.Register(nameof(Ticks), typeof(System.Windows.Media.DoubleCollection), typeof(ValueSlider),
                new PropertyMetadata(null));

        /// <summary>
        /// Explicit positions the thumb may take, for ranges that are not a uniform
        /// step. Combined with <see cref="IsSnapToTickEnabled"/> the thumb moves to the
        /// nearest listed value, so a gap in the range stays crossable in both
        /// directions instead of trapping the thumb at one end of it.
        /// </summary>
        public System.Windows.Media.DoubleCollection? Ticks
        {
            get => (System.Windows.Media.DoubleCollection?)GetValue(TicksProperty);
            set => SetValue(TicksProperty, value);
        }

        public static readonly DependencyProperty MarksProperty =
            DependencyProperty.Register(nameof(Marks), typeof(System.Windows.Media.DoubleCollection), typeof(ValueSlider),
                new PropertyMetadata(null));

        /// <summary>
        /// Values labelled on the scale under the track. Defaults to the minimum and
        /// maximum; set it to call out a boundary inside the range as well.
        /// </summary>
        public System.Windows.Media.DoubleCollection? Marks
        {
            get => (System.Windows.Media.DoubleCollection?)GetValue(MarksProperty);
            set => SetValue(MarksProperty, value);
        }

        public static readonly DependencyProperty ShowScaleProperty =
            DependencyProperty.Register(nameof(ShowScale), typeof(bool), typeof(ValueSlider),
                new PropertyMetadata(true));

        /// <summary>Whether to draw the labelled scale under the track.</summary>
        public bool ShowScale
        {
            get => (bool)GetValue(ShowScaleProperty);
            set => SetValue(ShowScaleProperty, value);
        }

        public static readonly DependencyProperty ScaleMarginProperty =
            DependencyProperty.Register(nameof(ScaleMargin), typeof(Thickness), typeof(ValueSlider),
                new PropertyMetadata(new Thickness(0, 1, 0, 0)));

        /// <summary>
        /// Spacing around the labelled scale below the track. Flyouts can use a tighter
        /// gap without changing the roomier slider rhythm on full application pages.
        /// </summary>
        public Thickness ScaleMargin
        {
            get => (Thickness)GetValue(ScaleMarginProperty);
            set => SetValue(ScaleMarginProperty, value);
        }

        public static readonly DependencyProperty FormatProperty =
            DependencyProperty.Register(nameof(Format), typeof(string), typeof(ValueSlider),
                new PropertyMetadata("{0}", OnDisplayInputChanged));

        /// <summary>Composite format string for the readout, for example "{0} W".</summary>
        public string Format
        {
            get => (string)GetValue(FormatProperty);
            set => SetValue(FormatProperty, value);
        }

        public static readonly DependencyProperty ValueTextProperty =
            DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(ValueSlider),
                new PropertyMetadata(null, OnDisplayInputChanged));

        /// <summary>
        /// Overrides the formatted readout. Used where the view model already knows how
        /// to word the value (for example "Default" instead of a clock in MHz).
        /// </summary>
        public string? ValueText
        {
            get => (string?)GetValue(ValueTextProperty);
            set => SetValue(ValueTextProperty, value);
        }

        private static readonly DependencyPropertyKey DisplayTextPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(DisplayText), typeof(string), typeof(ValueSlider),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

        /// <summary>Text actually shown in the readout column.</summary>
        public string DisplayText => (string)GetValue(DisplayTextProperty);

        private static void OnDisplayInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((ValueSlider)d).UpdateDisplayText();

        public ValueSlider()
        {
            UpdateDisplayText();
            Loaded += (_, _) =>
            {
                _rangeSettled = true;
                CoerceValue(ValueProperty);
            };
        }

        private void UpdateDisplayText()
        {
            if (!string.IsNullOrEmpty(ValueText))
            {
                SetValue(DisplayTextPropertyKey, ValueText);
                return;
            }

            string format = string.IsNullOrEmpty(Format) ? "{0}" : Format;
            double rounded = Math.Round(Value);

            try
            {
                SetValue(DisplayTextPropertyKey, string.Format(CultureInfo.CurrentCulture, format, rounded));
            }
            catch (FormatException)
            {
                SetValue(DisplayTextPropertyKey, rounded.ToString(CultureInfo.CurrentCulture));
            }
        }
    }
}
