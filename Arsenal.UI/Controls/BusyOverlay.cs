using Arsenal.Helpers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// Covers a window while something slow and uninterruptible runs, such as a GPU
    /// switch.
    ///
    /// The animation is driven from here rather than from style triggers on purpose.
    /// A <c>Trigger.EnterActions</c>/<c>BeginStoryboard</c> pair starts a fresh clock
    /// every time the trigger re-enters and never releases the previous one, so
    /// repeatedly showing the overlay piles up animation clocks that keep ticking for
    /// the life of the process - the UI gets progressively more stuttery the more
    /// switches you do. <see cref="UIElement.BeginAnimation(DependencyProperty, AnimationTimeline)"/>
    /// replaces the running clock on that property instead of stacking another one.
    /// </summary>
    public class BusyOverlay : System.Windows.Controls.Control
    {
        private FrameworkElement? _root;
        private ScaleTransform? _scale;

        public static readonly DependencyProperty FrameRateProperty =
            DependencyProperty.Register(nameof(FrameRate), typeof(int), typeof(BusyOverlay),
                new PropertyMetadata(60));

        /// <summary>
        /// Frames per second to ask for, for both this overlay's own transitions and the
        /// ring inside it. Worth lowering on a per-pixel-transparent window such as the
        /// quick panel, which WPF composes in software: there the full surface is
        /// re-rendered and copied for every frame, and asking for more of them than the
        /// window can produce only turns a steady animation into a stuttering one. It was
        /// asking for 120.
        /// </summary>
        public int FrameRate
        {
            get => (int)GetValue(FrameRateProperty);
            set => SetValue(FrameRateProperty, value);
        }

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(BusyOverlay),
                new PropertyMetadata(false, OnIsActiveChanged));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(nameof(Message), typeof(string), typeof(BusyOverlay),
                new PropertyMetadata(string.Empty));

        public string? Message
        {
            get => (string?)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        public static readonly DependencyProperty DetailProperty =
            DependencyProperty.Register(nameof(Detail), typeof(string), typeof(BusyOverlay),
                new PropertyMetadata("This can take a few seconds. Please wait."));

        public string? Detail
        {
            get => (string?)GetValue(DetailProperty);
            set => SetValue(DetailProperty, value);
        }

        static BusyOverlay()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(BusyOverlay),
                new FrameworkPropertyMetadata(typeof(BusyOverlay)));
        }

        public BusyOverlay()
        {
            // Idle overlays must not block clicks on the window underneath.
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            Focusable = false;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _root = GetTemplateChild("PART_Root") as FrameworkElement;
            _scale = GetTemplateChild("PART_Scale") as ScaleTransform;

            // Re-templating mid-flight (a theme switch) must not leave a stale state.
            if (_root is not null) _root.Opacity = IsActive ? 1 : 0;
            if (_scale is not null)
            {
                double start = IsActive ? 1 : 1.02;
                _scale.ScaleX = start;
                _scale.ScaleY = start;
            }
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var overlay = (BusyOverlay)d;
            if ((bool)e.NewValue) overlay.Show();
            else overlay.Hide();
        }

        private readonly FrameEase _fade = new();
        private readonly FrameEase _scaleXEase = new();
        private readonly FrameEase _scaleYEase = new();

        private void Show()
        {
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;

            if (_root is null || _scale is null) return;

            _fade.Start(_root.Opacity, 1, 260, FrameEase.CubicOut, v => _root.Opacity = v);
            _scaleXEase.Start(_scale.ScaleX, 1, 360, FrameEase.CubicOut, v => _scale.ScaleX = v);
            _scaleYEase.Start(_scale.ScaleY, 1, 360, FrameEase.CubicOut, v => _scale.ScaleY = v);
        }

        private void Hide()
        {
            IsHitTestVisible = false;

            if (_root is null || _scale is null)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            _scaleXEase.Start(_scale.ScaleX, 1.02, 300, FrameEase.CubicInOut, v => _scale.ScaleX = v);
            _scaleYEase.Start(_scale.ScaleY, 1.02, 300, FrameEase.CubicInOut, v => _scale.ScaleY = v);

            _fade.Start(
                _root.Opacity, 0, 300, FrameEase.CubicInOut,
                v => _root.Opacity = v,
                completed: () =>
                {
                    // A second switch may have started while this was fading out.
                    if (IsActive) return;

                    Visibility = Visibility.Collapsed;
                    _root.Opacity = 0;
                    _scale.ScaleX = 1.02;
                    _scale.ScaleY = 1.02;
                });
        }
    }
}
