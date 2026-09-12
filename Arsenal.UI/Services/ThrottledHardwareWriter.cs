using System.Threading.Tasks;

namespace Arsenal.UI.Services
{
    /// <summary>
    /// Turns a continuous stream of slider values into a bounded rate of hardware
    /// writes, off the UI thread.
    ///
    /// Dragging a slider produces a value on every mouse move, and the panel-brightness
    /// path is a synchronous WMI call, so writing each one would both stutter the drag
    /// and flood the driver. This writes the first value immediately so the change is
    /// visible at once, then at most one value per interval, and always finishes with
    /// the value the user settled on.
    /// </summary>
    public sealed class ThrottledHardwareWriter
    {
        private readonly Action<int> _write;
        private readonly int _minIntervalMs;
        private readonly object _gate = new();

        private int _latest;
        private bool _hasPending;
        private bool _isPumping;

        public ThrottledHardwareWriter(Action<int> write, int minIntervalMs = 80)
        {
            _write = write;
            _minIntervalMs = minIntervalMs;
        }

        /// <summary>
        /// Queues a value. Safe to call on every slider tick; only the newest value
        /// pending at any moment is ever written.
        /// </summary>
        public void Push(int value)
        {
            lock (_gate)
            {
                _latest = value;
                _hasPending = true;

                // A pump is already draining the queue and will pick this value up.
                if (_isPumping) return;
                _isPumping = true;
            }

            _ = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                int value;

                lock (_gate)
                {
                    if (!_hasPending)
                    {
                        _isPumping = false;
                        return;
                    }

                    value = _latest;
                    _hasPending = false;
                }

                try
                {
                    _write(value);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Throttled hardware write: " + ex.Message);
                }

                await Task.Delay(_minIntervalMs).ConfigureAwait(false);
            }
        }
    }
}
