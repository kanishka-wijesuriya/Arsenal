using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// A hairline that fills from the left to show how far through a sequence the user
    /// is, easing between values instead of jumping.
    ///
    /// The fill is a full-width bar under a horizontal <see cref="ScaleTransform"/>, so
    /// the animation runs on the composition thread and costs no layout passes. A plain
    /// bound Width would snap on every step and re-measure the footer with it.
    /// </summary>
    public class ProgressLine : System.Windows.Controls.Control
    {
        private const int FrameRate = 120;

        private ScaleTransform? _fill;

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProgressLine),
                new FrameworkPropertyMetadata(0d, OnValueChanged));

        /// <summary>How full the line is, from 0 to 1. Values outside are clamped.</summary>
        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        static ProgressLine()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ProgressLine),
                new FrameworkPropertyMetadata(typeof(ProgressLine)));
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _fill = GetTemplateChild("PART_Fill") as ScaleTransform;

            // First value arrives before the template does; show it without motion.
            if (_fill is not null) _fill.ScaleX = Clamp(Value);
        }

        private readonly FrameEase _fillEase = new();

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var line = (ProgressLine)d;
            if (line._fill is null) return;

            ScaleTransform fill = line._fill;
            line._fillEase.Start(
                fill.ScaleX,
                Clamp((double)e.NewValue),
                380,
                FrameEase.CubicOut,
                v => fill.ScaleX = v);
        }

        private static double Clamp(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
    }
}
