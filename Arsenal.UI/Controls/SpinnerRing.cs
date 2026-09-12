using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// An indeterminate ring: one arc rotating at a fixed rate.
    /// </summary>
    /// <remarks>
    /// Deliberately simpler than a stock progress ring, because of where it is used. The
    /// quick panel is a per-pixel-transparent window, which WPF composes in software: the
    /// whole surface is re-rendered and copied for every animation frame, so anything that
    /// spins continuously in front of it sets the frame cost for the entire panel. A ring
    /// built from two eased arcs re-tessellates a stroked, dashed geometry on each of those
    /// frames and could not hold the rate, which reads as a loader that is itself stuttering
    /// - exactly when the app is meant to look like it is working.
    ///
    /// One arc, one linear angle animation, a frame rate the caller can lower for a layered
    /// window, and the arc cached as a bitmap so a frame is a rotated blit rather than a
    /// fresh rasterisation. The clock is released when it stops, so an idle overlay is not
    /// still ticking behind a collapsed parent.
    /// </remarks>
    public class SpinnerRing : System.Windows.Controls.Control
    {
        private RotateTransform? _rotation;

        static SpinnerRing()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(SpinnerRing),
                new FrameworkPropertyMetadata(typeof(SpinnerRing)));
        }

        public static readonly DependencyProperty IsSpinningProperty =
            DependencyProperty.Register(nameof(IsSpinning), typeof(bool), typeof(SpinnerRing),
                new PropertyMetadata(false, OnSpinChanged));

        public bool IsSpinning
        {
            get => (bool)GetValue(IsSpinningProperty);
            set => SetValue(IsSpinningProperty, value);
        }

        public static readonly DependencyProperty FrameRateProperty =
            DependencyProperty.Register(nameof(FrameRate), typeof(int), typeof(SpinnerRing),
                new PropertyMetadata(60, OnSpinChanged));

        /// <summary>
        /// Frames per second to ask the animation clock for. A software-composed window
        /// cannot sustain the display rate, and a steady lower rate looks smoother than a
        /// higher one that keeps dropping frames.
        /// </summary>
        public int FrameRate
        {
            get => (int)GetValue(FrameRateProperty);
            set => SetValue(FrameRateProperty, value);
        }

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(SpinnerRing),
                new PropertyMetadata(4.0));

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        /// <summary>One turn. Slow enough to stay legible at a reduced frame rate.</summary>
        private static readonly Duration Turn = new(TimeSpan.FromMilliseconds(1100));

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _rotation = GetTemplateChild("PART_Rotation") as RotateTransform;
            ApplySpin();
        }

        private static void OnSpinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((SpinnerRing)d).ApplySpin();

        private void ApplySpin()
        {
            if (_rotation is null) return;

            if (!IsSpinning)
            {
                _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                _rotation.Angle = 0;
                return;
            }

            var spin = new DoubleAnimation(0, 360, Turn) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, Math.Clamp(FrameRate, 15, 120));
            _rotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
    }
}
