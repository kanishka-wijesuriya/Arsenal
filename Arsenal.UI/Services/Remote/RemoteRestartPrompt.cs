using Arsenal.Helpers;

namespace Arsenal.UI.Services.Remote
{
    /// <summary>A question the desktop needs answered before it can continue.</summary>
    public sealed record RemotePrompt(string Id, string Title, string Body, string Confirm, string Cancel);

    /// <summary>
    /// Routes a confirmation to the phone when the phone is what asked for the change.
    /// </summary>
    /// <remarks>
    /// Switching into or out of Ultimate reboots Windows, so the desktop asks first. That
    /// confirmation was a modal on the PC, which is no use to somebody holding the phone:
    /// the request appeared to hang while a dialog nobody could see waited on a machine
    /// across the room.
    ///
    /// Companion commands run on their own connection task, so the request that needs an
    /// answer can simply wait for one. The prompt is published in the state every poll
    /// picks up, the phone answers it, and the waiting command carries on or backs out.
    /// </remarks>
    public static class RemoteRestartPrompt
    {
        /// <summary>
        /// Marks the current asynchronous flow as executing a companion command, so a
        /// confirmation raised anywhere underneath it is asked of the phone.
        /// </summary>
        /// <remarks>
        /// An <see cref="AsyncLocal{T}"/> rather than a <c>[ThreadStatic]</c>. The flag has
        /// to survive both an <c>await</c> and the hop onto the worker that runs the GPU
        /// switch, neither of which a thread-local does. A thread-local was also unsafe in
        /// the other direction: it was set on the UI thread, so a command that threw after
        /// an await left it stuck true there, and the next switch made *at the machine*
        /// silently went looking for a phone to answer it.
        ///
        /// Assignments inside an async method are undone for its caller when the state
        /// machine yields, so the scope cannot leak out of the command that opened it.
        /// </remarks>
        private static readonly AsyncLocal<bool> OnCompanionFlow = new();

        private static readonly object Gate = new();
        private static RemotePrompt? _current;
        private static ManualResetEventSlim? _answered;
        private static bool _accepted;

        /// <summary>Longer than a considered answer takes, short enough to release the
        /// connection if the phone is put down mid-question.</summary>
        private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(90);

        public static bool IsCompanionCommand => OnCompanionFlow.Value;

        /// <summary>The question awaiting an answer, or null. Published in the state.</summary>
        public static RemotePrompt? Current
        {
            get { lock (Gate) return _current; }
        }

        public static IDisposable CommandScope() => new Scope();

        /// <summary>
        /// Publishes a question and blocks this command until the phone answers, the
        /// timeout expires, or another prompt supersedes it.
        /// </summary>
        public static bool Ask(string title, string body, string? confirm = null, string? cancel = null)
        {
            // Resource lookups are not compile-time constants, so the button labels
            // default here rather than in the signature.
            confirm ??= "Restart now";
            cancel ??= "Not now";

            // Waiting here holds whatever thread called in, for up to the timeout. On the
            // dispatcher that is fatal rather than slow: the answer arrives as another
            // companion command, the published prompt reaches the phone through a snapshot,
            // and both of those need the same thread this would be sitting on - so the
            // question could never be seen, let alone answered, and the window froze until
            // the timeout expired. Callers are expected to be off the UI thread; refusing
            // is the safe reading of an unanswerable question.
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                Logger.WriteLine("Companion prompt refused: cannot wait for an answer on the UI thread - " + title);
                return false;
            }

            var waiter = new ManualResetEventSlim(false);
            var prompt = new RemotePrompt(Guid.NewGuid().ToString("N"), title, body, confirm, cancel);

            lock (Gate)
            {
                // A second question replaces the first; the abandoned one is released so
                // its command does not sit on a connection thread until the timeout.
                _answered?.Set();
                _current = prompt;
                _answered = waiter;
                _accepted = false;
            }

            RemoteStateSignal.Bump();

            bool signalled = waiter.Wait(AnswerTimeout);

            lock (Gate)
            {
                bool accepted = signalled && _accepted;
                if (ReferenceEquals(_answered, waiter))
                {
                    _current = null;
                    _answered = null;
                    RemoteStateSignal.Bump();
                }

                if (!signalled) Logger.WriteLine("Companion prompt timed out: " + title);
                return accepted;
            }
        }

        /// <summary>Answers the outstanding question. Ignores an answer to one already gone.</summary>
        public static void Answer(string? id, bool accepted)
        {
            lock (Gate)
            {
                if (_current is null || _answered is null) return;
                if (!string.IsNullOrEmpty(id) && !string.Equals(id, _current.Id, StringComparison.Ordinal)) return;

                _accepted = accepted;
                _answered.Set();
            }
        }

        private sealed class Scope : IDisposable
        {
            public Scope() => OnCompanionFlow.Value = true;
            public void Dispose() => OnCompanionFlow.Value = false;
        }
    }
}
