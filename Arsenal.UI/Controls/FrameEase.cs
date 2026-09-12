using System.Diagnostics;
using System.Windows.Media;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// Eases a double from one value to another, producing one new value on every
    /// presented frame.
    /// </summary>
    /// <remarks>
    /// A replacement for <see cref="System.Windows.Media.Animation.DoubleAnimation"/> on
    /// the short, long-distance motions in this app. Measured against the tile page slide -
    /// 320ms, ~420px, over a real content tree - a WPF timeline produced 1 to 5 moving
    /// values while 11 to 19 presented frames showed no change at all, so the slide
    /// arrived as two or three large jumps. The same motion driven from
    /// <see cref="CompositionTarget.Rendering"/> produced 38 to 43 moving values with a
    /// step deviation around 5px instead of 150px. The gap held in both a layered and a
    /// normal window, so it is the timeline clock rather than the Quick Panel's
    /// per-pixel-transparent surface.
    ///
    /// Interpolation is taken from a <see cref="Stopwatch"/> rather than a frame count, so
    /// a dropped frame costs a frame of smoothness and never lengthens the motion.
    /// </remarks>
    public sealed class FrameEase
    {
        private readonly Stopwatch _clock = new();
        private EventHandler? _tick;
        private double _from;
        private double _to;
        private double _durationMs;
        private double _delayMs;
        private Func<double, double> _easing = Linear;
        private Action<double>? _apply;
        private Action? _completed;

        public bool IsRunning { get; private set; }

        /// <summary>The value the running ease is heading for.</summary>
        public double Target => _to;

        /// <summary>Symmetric acceleration and deceleration; the stock choice for a slide.</summary>
        public static double SineInOut(double progress)
            => 0.5d * (1d - Math.Cos(progress * Math.PI));

        public static double Linear(double progress) => progress;

        public static double CubicIn(double progress) => progress * progress * progress;

        public static double CubicOut(double progress)
            => 1d - Math.Pow(1d - progress, 3d);

        public static double CubicInOut(double progress)
            => progress < 0.5d
                ? 4d * progress * progress * progress
                : 1d - (Math.Pow((-2d * progress) + 2d, 3d) / 2d);

        public static double QuarticIn(double progress)
            => progress * progress * progress * progress;

        public static double QuinticOut(double progress)
            => 1d - Math.Pow(1d - progress, 5d);

        /// <summary>
        /// Starts, or restarts, the ease. Calling this while one is running replaces it
        /// outright, so callers that want continuity should pass the current on-screen
        /// value as <paramref name="from"/>.
        /// </summary>
        public void Start(
            double from,
            double to,
            double durationMs,
            Func<double, double> easing,
            Action<double> apply,
            Action? completed = null,
            double delayMs = 0d)
        {
            _from = from;
            _to = to;
            _durationMs = Math.Max(1d, durationMs);
            _delayMs = Math.Max(0d, delayMs);
            _easing = easing;
            _apply = apply;
            _completed = completed;

            apply(from);
            _clock.Restart();

            if (IsRunning)
                return;

            IsRunning = true;
            _tick ??= OnRendering;
            CompositionTarget.Rendering += _tick;
        }

        /// <summary>
        /// Ends the ease where it stands. The apply callback is not invoked again, so the
        /// caller keeps whatever is currently on screen.
        /// </summary>
        public void Stop()
        {
            if (!IsRunning)
                return;

            IsRunning = false;
            _clock.Reset();
            if (_tick is not null)
                CompositionTarget.Rendering -= _tick;
        }

        private void OnRendering(object? sender, EventArgs args)
        {
            // CompositionTarget.Rendering is a multicast delegate and WPF invokes a
            // snapshot of it, so unsubscribing from inside one handler does not stop the
            // handlers already scheduled for this frame. Without this guard a stopped
            // ease still ran once more - and because Stop zeroes the clock, it ran at
            // progress 0 and wrote its *start* value back. One ease completing and
            // stopping its siblings therefore undid whatever the completion handler had
            // just set, which is how a finished view transition ended up with the card at
            // its new height and the content host back at its old one.
            if (!IsRunning)
                return;

            // A stagger holds the start value rather than jumping, which is what lets
            // several eases overlap into one composed transition.
            double elapsed = _clock.Elapsed.TotalMilliseconds - _delayMs;
            if (elapsed < 0d)
                return;

            double progress = elapsed / _durationMs;

            if (progress >= 1d)
            {
                _apply?.Invoke(_to);
                Action? completed = _completed;
                Stop();
                completed?.Invoke();
                return;
            }

            _apply?.Invoke(_from + ((_to - _from) * _easing(progress)));
        }
    }
}
