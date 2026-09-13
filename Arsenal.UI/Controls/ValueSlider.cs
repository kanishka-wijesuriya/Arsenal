using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Size = System.Windows.Size;

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

            // While the thumb is held, the only value the slider accepts is one the thumb
            // itself produced. See HoldSettleTime for what this is keeping out, and
            // MarkThumbSteps for how a step of the drag is told apart from a write that
            // arrived from somewhere else.
            if (slider._held && !slider._steppingFromThumb) return slider.Value;

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
            slider.UpdateEndText();
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

        public static readonly DependencyProperty ShowValueTooltipProperty =
            DependencyProperty.Register(nameof(ShowValueTooltip), typeof(bool), typeof(ValueSlider),
                new PropertyMetadata(true, OnShowValueTooltipChanged));

        /// <summary>
        /// Whether dragging the thumb raises a bubble above it with the value being set.
        /// </summary>
        /// <remarks>
        /// On by default. A drag puts the pointer on the thumb and keeps it there, which
        /// is the one part of the row the value was never written on - and a compact
        /// surface such as the quick panel drops the readout column altogether, so while
        /// the drag was in progress there was nowhere to read the value from at all.
        /// </remarks>
        public bool ShowValueTooltip
        {
            get => (bool)GetValue(ShowValueTooltipProperty);
            set => SetValue(ShowValueTooltipProperty, value);
        }

        private static void OnShowValueTooltipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(bool)e.NewValue) ((ValueSlider)d).CloseValueTooltip();
        }

        public static readonly DependencyProperty FormatProperty =
            DependencyProperty.Register(nameof(Format), typeof(string), typeof(ValueSlider),
                new PropertyMetadata("{0}", OnFormatChanged));

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

        private static readonly DependencyPropertyKey MinimumTextPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(MinimumText), typeof(string), typeof(ValueSlider),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty MinimumTextProperty = MinimumTextPropertyKey.DependencyProperty;

        /// <summary>
        /// The lowest value the slider accepts, worded the way the readout words the
        /// current one. Surfaces that drop the readout label the two ends of the track
        /// with this and <see cref="MaximumText"/> instead.
        /// </summary>
        public string MinimumText => (string)GetValue(MinimumTextProperty);

        private static readonly DependencyPropertyKey MaximumTextPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(MaximumText), typeof(string), typeof(ValueSlider),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty MaximumTextProperty = MaximumTextPropertyKey.DependencyProperty;

        /// <summary>The highest value the slider accepts. See <see cref="MinimumText"/>.</summary>
        public string MaximumText => (string)GetValue(MaximumTextProperty);

        private static void OnDisplayInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((ValueSlider)d).UpdateDisplayText();

        private static void OnFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var slider = (ValueSlider)d;
            slider.UpdateDisplayText();
            slider.UpdateEndText();
        }

        public ValueSlider()
        {
            UpdateDisplayText();
            UpdateEndText();
            Loaded += (_, _) =>
            {
                _rangeSettled = true;
                CoerceValue(ValueProperty);
            };

            // A popup is a window of its own, so nothing takes the bubble down when the
            // control that owns it stops being shown. The quick panel is dismissed by
            // clicking away from it, and that click can land mid-drag - which would
            // leave the bubble floating over the desktop with nothing underneath it.
            Unloaded += (_, _) =>
            {
                CloseValueTooltip();
                EndHold();
            };
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) return;
                CloseValueTooltip();
                EndHold();
            };
        }

        private void UpdateDisplayText()
        {
            SetValue(
                DisplayTextPropertyKey,
                string.IsNullOrEmpty(ValueText) ? FormatValue(Value) : ValueText);

            // The bubble carries the same string the readout column does, so it is
            // refreshed from the same place. Value changes once per step of a drag,
            // which is exactly how often the bubble has to be reworded and moved.
            RefreshValueTooltip();
        }

        private void UpdateEndText()
        {
            SetValue(MinimumTextPropertyKey, FormatValue(Minimum));
            SetValue(MaximumTextPropertyKey, FormatValue(Maximum));
        }

        private string FormatValue(double value)
        {
            string format = string.IsNullOrEmpty(Format) ? "{0}" : Format;
            double rounded = Math.Round(value);

            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, rounded);
            }
            catch (FormatException)
            {
                return rounded.ToString(CultureInfo.CurrentCulture);
            }
        }

        // ===================================================================
        // The drag bubble
        //
        // A plain Slider can raise its own tooltip on a drag - AutoToolTipPlacement -
        // but its content is the raw number and nothing can reach inside it: "65" would
        // appear under a track whose readout says "65%", and under one whose readout
        // says "Default". The bubble is built here instead, from the same DisplayText
        // the readout column shows.
        //
        // It is created in code rather than declared as a template part because
        // ValueSlider has two templates - the shared one in Components.xaml and the
        // compact one the quick panel replaces it with - and a bubble that exists only
        // in whichever template remembered to declare it is the bug being fixed.
        // ===================================================================

        /// <summary>Clearance between the bottom of the bubble and the top of the track.</summary>
        private const double TooltipGap = 8;

        private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

        /// <summary>
        /// How long after the pointer lets go the value stays the user's.
        /// </summary>
        /// <remarks>
        /// A slider bound to hardware hears its own writes come back. The panel is asked
        /// for 66%, reports the level it reached, and that report arrives when the drag
        /// has already moved on to 70% - so applying it pulls the thumb back to where the
        /// pointer was two steps ago. Worse, WPF works out the next step of a drag from
        /// the value it finds rather than from the pointer, so the drag then carries on
        /// from the stale number instead of snapping back. That is the twitching, and the
        /// brightness slider has it worst because a panel write is the slowest of them.
        ///
        /// The reports do not stop when the button comes up - the last write of a drag
        /// has not even been made by then, since writes are throttled to 80ms - so the
        /// hold has to outlast the drag by enough for the tail of them to land. This is
        /// the whole of that tail with room to spare, and it is short enough that a
        /// brightness key pressed straight after a drag still reads as immediate.
        /// </remarks>
        private static readonly TimeSpan HoldSettleTime = TimeSpan.FromMilliseconds(750);

        private bool _held;
        private DispatcherTimer? _holdTimer;
        private bool _steppingFromThumb;
        private Track? _markedTrack;

        private Slider? _slider;
        private Popup? _valueTooltip;
        private Border? _valueTooltipChrome;
        private TextBlock? _valueTooltipText;

        /// <summary>
        /// The bubble, once a drag has called for one, and null until then. There is
        /// nothing to set here: it is exposed so that the slider smoke tool can check
        /// what a drag actually raises rather than that the code to raise it compiles.
        /// </summary>
        public Popup? ValueTooltip => _valueTooltip;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_slider is not null)
            {
                _slider.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnThumbDragStarted));
                _slider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnThumbDragCompleted));
                _slider.RemoveHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler(OnStepApplied));
            }

            _markedTrack?.RemoveHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler(OnStepLeavingThumb));
            _markedTrack = null;

            CloseValueTooltip();
            EndHold();
            _slider = GetTemplateChild("PART_Slider") as Slider;
            if (_slider is null) return;

            // handledEventsToo: Slider registers class handlers for both of these to run
            // its own drag, and a class handler is ahead of every instance handler on
            // the element whatever it then does with the event.
            _slider.AddHandler(
                Thumb.DragStartedEvent, new DragStartedEventHandler(OnThumbDragStarted), handledEventsToo: true);
            _slider.AddHandler(
                Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnThumbDragCompleted), handledEventsToo: true);
        }

        private void OnThumbDragStarted(object sender, DragStartedEventArgs e)
        {
            // Before the tooltip, and whether or not there is one: the hold is what keeps
            // the thumb under the pointer, and a surface that turned the bubble off still
            // needs it.
            _held = true;
            _holdTimer?.Stop();
            MarkThumbSteps();

            if (!ShowValueTooltip || !IsEnabled) return;

            Popup tooltip = EnsureValueTooltip();
            _valueTooltipText!.Text = DisplayText;

            // Nothing to point at yet - an unmeasured track would put the bubble at the
            // left end of a slider the user is already dragging in the middle of.
            if (!PositionValueTooltip()) return;

            tooltip.IsOpen = true;
        }

        private void OnThumbDragCompleted(object sender, DragCompletedEventArgs e)
        {
            CloseValueTooltip();

            // The hold outlasts the drag: the reports the drag provoked are still on
            // their way up when the button comes up.
            _holdTimer ??= CreateHoldTimer();
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        /// <summary>
        /// Brackets each step of a drag, so a held slider can tell the user's own steps
        /// from a write that arrived from anywhere else.
        /// </summary>
        /// <remarks>
        /// The obvious test - does the value being offered match the one the inner slider
        /// is showing - does not work: the binding carries the new value outwards before
        /// the inner slider will report it, so every step of a drag looks like an outside
        /// write and the thumb cannot be moved at all.
        ///
        /// What does work is where the step is in the air. A drag reaches the slider as a
        /// DragDelta bubbling up from the thumb, and it is the slider that acts on it, in
        /// a class handler. So a handler on the track - passed on the way up, before the
        /// slider - opens the window, and an instance handler on the slider closes it
        /// again, class handlers being ahead of instance handlers on the same element.
        /// Anything setting the value outside that window came from somewhere else.
        /// </remarks>
        private void MarkThumbSteps()
        {
            if (_slider is null) return;

            Track? track = FindTrack(_slider);
            if (track is null || ReferenceEquals(track, _markedTrack)) return;

            _markedTrack = track;
            track.AddHandler(
                Thumb.DragDeltaEvent, new DragDeltaEventHandler(OnStepLeavingThumb), handledEventsToo: true);
            _slider.AddHandler(
                Thumb.DragDeltaEvent, new DragDeltaEventHandler(OnStepApplied), handledEventsToo: true);
        }

        private void OnStepLeavingThumb(object sender, DragDeltaEventArgs e) => _steppingFromThumb = true;

        private void OnStepApplied(object sender, DragDeltaEventArgs e) => _steppingFromThumb = false;

        private DispatcherTimer CreateHoldTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = HoldSettleTime };
            timer.Tick += (_, _) => EndHold();
            return timer;
        }

        private void EndHold()
        {
            _holdTimer?.Stop();
            _held = false;
            _steppingFromThumb = false;
        }

        private void RefreshValueTooltip()
        {
            if (_valueTooltip is null || !_valueTooltip.IsOpen) return;

            _valueTooltipText!.Text = DisplayText;
            PositionValueTooltip();
        }

        private void CloseValueTooltip()
        {
            if (_valueTooltip is not null) _valueTooltip.IsOpen = false;
        }

        /// <summary>
        /// Centres the bubble over the thumb. Returns false while the track has no
        /// geometry to place it against.
        /// </summary>
        /// <remarks>
        /// The thumb's own position is read from the value rather than from the visual
        /// tree: this runs as the value changes, which is before the layout pass that
        /// moves the thumb, so asking the thumb where it is would answer with where it
        /// was a step ago and the bubble would trail the drag by one step.
        /// </remarks>
        private bool PositionValueTooltip()
        {
            if (_valueTooltip is null || _valueTooltipChrome is null || _slider is null) return false;

            Track? track = FindTrack(_slider);
            if (track is null || track.ActualWidth <= 0) return false;

            double thumbWidth = track.Thumb?.ActualWidth ?? 0;
            double span = Maximum - Minimum;
            double progress = span > 0 ? (Value - Minimum) / span : 0;
            progress = progress < 0 ? 0 : progress > 1 ? 1 : progress;

            // Where Track puts the thumb on a horizontal slider: half a thumb in from
            // each end, and the value's share of whatever is left between them.
            double centre = (thumbWidth / 2) + (progress * (track.ActualWidth - thumbWidth));

            // The bubble is centred on that, so its own width has to be known before it
            // can be placed - and the width moves with the number, from "5%" to "100%".
            _valueTooltipChrome.Measure(Unbounded);
            Size bubble = _valueTooltipChrome.DesiredSize;

            _valueTooltip.PlacementTarget = track;
            _valueTooltip.HorizontalOffset = centre - (bubble.Width / 2);
            _valueTooltip.VerticalOffset = -(bubble.Height + TooltipGap);
            return true;
        }

        private Popup EnsureValueTooltip()
        {
            if (_valueTooltip is not null) return _valueTooltip;

            _valueTooltipText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center
            };
            _valueTooltipText.SetResourceReference(TextBlock.ForegroundProperty, "AccentForeground");

            // The accent pair, not the tooltip surface: the bubble sits directly over a
            // card painted in SurfaceCard, which is the tooltip's own background, and
            // the accent is already the colour of the filled part of the track it
            // belongs to. AccentForeground is computed for contrast against it.
            _valueTooltipChrome = new Border
            {
                MinWidth = 34,
                Padding = new Thickness(8, 3, 8, 4),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                Child = _valueTooltipText
            };
            _valueTooltipChrome.SetResourceReference(Border.BackgroundProperty, "AccentPrimary");
            _valueTooltipChrome.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtle");
            _valueTooltipChrome.SetResourceReference(Border.CornerRadiusProperty, "RadiusControl");

            _valueTooltip = new Popup
            {
                Child = _valueTooltipChrome,
                Placement = PlacementMode.Relative,
                AllowsTransparency = true,
                StaysOpen = true,
                Focusable = false,
                PopupAnimation = PopupAnimation.Fade
            };

            return _valueTooltip;
        }

        private static Track? FindTrack(Slider slider)
        {
            try
            {
                if (slider.Template?.FindName("PART_Track", slider) is Track named) return named;
            }
            catch (InvalidOperationException)
            {
                // The template has not been applied to this slider yet. The visual walk
                // below answers the same question without depending on the part name.
            }

            return FindDescendant<Track>(slider);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                if (FindDescendant<T>(child) is T nested) return nested;
            }

            return null;
        }
    }
}
