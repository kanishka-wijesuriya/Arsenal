using Arsenal.Helpers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// Keeps the desktop clipboard and the phone's in step, in both directions.
/// </summary>
/// <remarks>
/// Text only. An image or a file list would have to be re-encoded for Android's
/// clipboard, which has no equivalent for either, and the file channel already moves
/// files with a progress bar and a cancel button rather than silently through a paste.
///
/// <para>The desktop side is polled rather than hooked. A clipboard format listener
/// needs a window handle that outlives the session and a WM_CLIPBOARDUPDATE pump, and
/// GetClipboardSequenceNumber answers the same question for the cost of a syscall
/// without opening the clipboard, which is the part that fails when another application
/// is holding it. Twice a second is faster than anyone notices and cheap enough to leave
/// running for the length of a session.</para>
///
/// <para>Everything touching the clipboard itself runs on the UI dispatcher: the
/// clipboard is an STA API and calling it from the session's socket thread either throws
/// or, worse, works until it does not.</para>
/// </remarks>
internal sealed class RemoteClipboardBridge : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Action<string> _onDesktopCopied;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _timer;
    private uint _lastSequence;
    private string _lastSent = string.Empty;
    private string _lastReceived = string.Empty;
    private bool _disposed;

    internal RemoteClipboardBridge(Dispatcher dispatcher, Action<string> onDesktopCopied)
    {
        _dispatcher = dispatcher;
        _onDesktopCopied = onDesktopCopied;
    }

    internal void Start() => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;

        // Seed from the current state rather than from zero, so a session does not open
        // by pushing whatever happened to be on the desktop clipboard hours ago.
        _lastSequence = GetClipboardSequenceNumber();
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = PollInterval };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    });

    private void Poll()
    {
        if (_disposed) return;
        try
        {
            uint sequence = GetClipboardSequenceNumber();
            if (sequence == _lastSequence) return;
            _lastSequence = sequence;

            if (!System.Windows.Clipboard.ContainsText()) return;
            string text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength) return;

            // The change this bridge caused itself, coming back around.
            if (text == _lastReceived || text == _lastSent) return;

            _lastSent = text;
            _onDesktopCopied(text);
        }
        catch (Exception ex)
        {
            // Another application holding the clipboard is ordinary, not an error worth
            // a line per attempt; only say so once per session's worth of failures.
            Logger.WriteLine("Remote clipboard read: " + ex.Message);
        }
    }

    /// <summary>Puts what the phone copied onto the desktop clipboard.</summary>
    internal void Receive(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text) || text.Length > MaxTextLength) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            try
            {
                _lastReceived = text;
                System.Windows.Clipboard.SetText(text);
                _lastSequence = GetClipboardSequenceNumber();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Remote clipboard write: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// A megabyte of text.
    /// </summary>
    /// <remarks>
    /// Far past any clipboard anyone uses deliberately, and short of the point where
    /// synchronising it every half second would be felt. A larger copy is left on the
    /// machine it was made on rather than truncated, because a silently shortened paste
    /// is worse than none.
    /// </remarks>
    private const int MaxTextLength = 1024 * 1024;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dispatcher.BeginInvoke(() =>
        {
            _timer?.Stop();
            _timer = null;
        });
    }
}
