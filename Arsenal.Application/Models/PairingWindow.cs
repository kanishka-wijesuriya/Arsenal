using System.Security.Cryptography;
using System.Text;

namespace Arsenal.Application.Models
{
    /// <summary>
    /// When the companion bridge will accept a pairing attempt, and for how many wrong
    /// answers.
    /// </summary>
    /// <remarks>
    /// Pairing used to be permanently available: the bridge served <c>/v1/pair</c> from
    /// the moment it started, a code was minted on demand whenever the previous one
    /// expired, including in response to an incoming request, so a caller could create
    /// the very credential it was guessing at, and a wrong code returned 403 with no
    /// counter, no delay and no lockout.
    ///
    /// <para>Six digits is a million possibilities. Against unlimited guesses over a LAN
    /// that is not a secret; anyone on the same network could pair themselves with the
    /// machine in a few minutes. What makes a short code safe is a small number of
    /// attempts inside a window a person deliberately opened.</para>
    ///
    /// <para>This lives here, apart from the service, because it is the highest
    /// consequence branch in the companion and it should be possible to test it without
    /// a socket, a certificate or a phone. <see cref="Arsenal.Tests"/> does.</para>
    /// </remarks>
    public sealed class PairingWindow
    {
        /// <summary>How long the window stays open after the pairing screen last asked.</summary>
        public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(5);

        /// <summary>How long a single code stays valid, even inside an open window.</summary>
        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

        /// <summary>Wrong codes allowed before the window closes and the code is discarded.</summary>
        public const int MaxFailures = 5;

        private readonly object _gate = new();
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<string> _newCode;

        private string _code = string.Empty;
        private DateTimeOffset _codeExpires;
        private DateTimeOffset _windowExpires;
        private int _failures;

        /// <param name="now">The clock. Injected so tests need not wait five minutes.</param>
        /// <param name="newCode">Code generator. Injected so a test can know the answer.</param>
        public PairingWindow(Func<DateTimeOffset>? now = null, Func<string>? newCode = null)
        {
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _newCode = newCode ?? (() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"));
        }

        /// <summary>True while an attempt could possibly succeed.</summary>
        public bool IsOpen
        {
            get { lock (_gate) return IsOpenLocked(); }
        }

        /// <summary>
        /// The current code, opening or extending the window as a side effect.
        /// </summary>
        /// <remarks>
        /// Only the pairing screen reads this, and it re-reads on a tick while it is on
        /// screen, so reading it is the signal that somebody is trying to pair. Nothing on
        /// the network path may call this: an incoming request must never be able to bring
        /// a code into existence.
        /// </remarks>
        public string Peek()
        {
            lock (_gate)
            {
                DateTimeOffset now = _now();
                if (_code.Length == 0 || now >= _codeExpires) IssueLocked(now);
                _windowExpires = now + DefaultDuration;
                return _code;
            }
        }

        /// <summary>Issues a fresh code and opens the window. The refresh button.</summary>
        public string Reissue()
        {
            lock (_gate)
            {
                DateTimeOffset now = _now();
                IssueLocked(now);
                _windowExpires = now + DefaultDuration;
                return _code;
            }
        }

        /// <summary>
        /// Closes the window and discards the code. Called when the pairing screen goes
        /// away, after a successful pairing, and after too many failures.
        /// </summary>
        public void Close()
        {
            lock (_gate)
            {
                _code = string.Empty;
                _codeExpires = default;
                _windowExpires = default;
                _failures = 0;
            }
        }

        /// <summary>Checks a presented code and records the outcome.</summary>
        public PairingAttempt Submit(string? presented)
        {
            lock (_gate)
            {
                if (!IsOpenLocked()) return PairingAttempt.Rejected;

                // Fixed-time so the answer cannot be recovered a digit at a time.
                bool accepted = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes((presented ?? string.Empty).PadLeft(6, '0')),
                    Encoding.UTF8.GetBytes(_code));

                if (accepted)
                {
                    // Consumed while still holding the gate, so two requests racing can
                    // never both pair with the same one-time credential. Pairing one phone
                    // also ends the window: the next needs the screen opened again.
                    Close();
                    return PairingAttempt.Accepted;
                }

                if (++_failures >= MaxFailures)
                {
                    Close();
                    return PairingAttempt.LockedOut;
                }

                return PairingAttempt.Rejected;
            }
        }

        private bool IsOpenLocked()
        {
            DateTimeOffset now = _now();
            return now < _windowExpires && _code.Length > 0 && now < _codeExpires;
        }

        private void IssueLocked(DateTimeOffset now)
        {
            _code = _newCode();
            _codeExpires = now + CodeLifetime;
            _failures = 0;
        }
    }

    public enum PairingAttempt
    {
        /// <summary>Wrong code, or the window is not open. The caller is told neither which.</summary>
        Rejected,

        /// <summary>Correct, and consumed.</summary>
        Accepted,

        /// <summary>Wrong, and that was the last attempt this window gets.</summary>
        LockedOut,
    }
}
