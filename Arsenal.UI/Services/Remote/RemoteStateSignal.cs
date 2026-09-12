namespace Arsenal.UI.Services.Remote
{
    /// <summary>
    /// Tracks that something a phone cares about has changed, and lets a request wait for
    /// the next change instead of asking again on a timer.
    /// </summary>
    /// <remarks>
    /// The companion polled every two seconds, so a mode changed on the PC took up to that
    /// long to appear on the phone - and if the poll landed mid-switch it reported the old
    /// value and then sat on it until the next one. Every desktop control already raises an
    /// event; bumping a version from those and holding the phone's request open until the
    /// version moves turns the same endpoint into a push without changing its shape.
    /// </remarks>
    public static class RemoteStateSignal
    {
        private static readonly object Gate = new();
        private static TaskCompletionSource _changed = Fresh();
        private static int _version;

        /// <summary>Increments whenever anything in the published state changes.</summary>
        public static int Version
        {
            get { lock (Gate) return _version; }
        }

        /// <summary>Records a change and releases every request waiting on one.</summary>
        public static void Bump()
        {
            TaskCompletionSource released;

            lock (Gate)
            {
                _version++;
                released = _changed;
                _changed = Fresh();
            }

            // Outside the lock: continuations run inline otherwise, on a hardware event
            // thread that has no business serving HTTP responses.
            released.TrySetResult();
        }

        /// <summary>
        /// Waits until the version differs from <paramref name="since"/>, or the timeout
        /// expires. Returns immediately when the caller is already behind.
        /// </summary>
        public static async Task WaitForChangeAsync(int since, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task changed;

            lock (Gate)
            {
                if (_version != since) return;
                changed = _changed.Task;
            }

            using var timer = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);

            // A timeout is a normal outcome, not a failure: it becomes a heartbeat
            // response that tells the phone the connection is still good.
            await Task.WhenAny(changed, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
        }

        private static TaskCompletionSource Fresh() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
