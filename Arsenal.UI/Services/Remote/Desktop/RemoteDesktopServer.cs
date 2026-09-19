using Arsenal.Helpers;
using Arsenal.UI.Views.Windows;
using System.IO;
using System.Windows.Threading;

namespace Arsenal.UI.Services.Remote.Desktop;

internal readonly record struct RemoteConsentDecision(bool Allowed, bool ViewOnly, string? Reason);

/// <summary>
/// Every remote control session this desktop is hosting, and the rules for starting one.
/// </summary>
/// <remarks>
/// The companion bridge owns the socket and the pairing; this owns what happens after a
/// paired phone asks for the screen. Keeping the two apart means the rest of the
/// companion - snapshots, commands, the catalog - is unchanged by this existing, and a
/// desktop with the feature turned off runs exactly the code it ran before.
/// </remarks>
public sealed class RemoteDesktopServer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<RemoteDesktopSession> _sessions = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _remembered = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// At most this many phones watching at once.
    /// </summary>
    /// <remarks>
    /// Each session holds a capture surface, an encoder and a thread. Two is enough for
    /// a phone and a tablet; a third is far more likely to be a stuck session than a
    /// third device somebody meant to open.
    /// </remarks>
    private const int MaxSessions = 2;

    public RemoteDesktopServer() : this(System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher) { }

    internal RemoteDesktopServer(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        foreach (string id in (AppConfig.GetString(RememberedKey) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _remembered.Add(id);
        }
    }

    private const string RememberedKey = "remote_desktop_trusted";

    /// <summary>Raised whenever a session starts, stops or changes what it is doing.</summary>
    public event EventHandler? SessionsChanged;

    public bool IsEnabled => RemoteDesktopSettings.Enabled;

    public int ActiveSessions
    {
        get { lock (_gate) return _sessions.Count; }
    }

    /// <summary>A one line description of what is happening, for the companion page.</summary>
    public string StatusLine
    {
        get
        {
            lock (_gate)
            {
                if (!RemoteDesktopSettings.Enabled) return "Off";
                if (_sessions.Count == 0) return "Ready";
                RemoteDesktopSession session = _sessions[0];
                string who = session.Peer.DeviceName;
                if (_sessions.Count > 1) who += " and " + (_sessions.Count - 1) + " more";
                return session.Streaming
                    ? who + " is controlling this PC" + (session.ViewOnly ? " (view only)" : string.Empty)
                    : who + " is connected";
            }
        }
    }

    // ---- Sessions ----------------------------------------------------------------

    /// <summary>
    /// Takes over a connection the companion bridge has already authenticated.
    /// </summary>
    /// <remarks>
    /// The bridge has checked the bearer token and the phone has pinned the certificate,
    /// so the stream handed over here is already the same trust boundary every other
    /// companion request runs inside. What is not established yet is consent, and that
    /// happens when the phone asks for the screen rather than here.
    /// </remarks>
    internal async Task AcceptAsync(Stream stream, RemotePeer peer)
    {
        if (!RemoteDesktopSettings.Enabled)
        {
            Logger.WriteLine("Remote session refused: turned off on this PC.");
            return;
        }

        RemoteDesktopSession session;
        lock (_gate)
        {
            if (_sessions.Count >= MaxSessions)
            {
                Logger.WriteLine("Remote session refused: " + MaxSessions + " already running.");
                return;
            }
            session = new RemoteDesktopSession(stream, peer, _dispatcher, this, _stopping.Token);
            _sessions.Add(session);
        }

        NotifySessionsChanged();
        Logger.WriteLine("Remote session opened for " + peer.DeviceName + " at " + peer.Address);
        try { await session.RunAsync(); }
        finally { Logger.WriteLine("Remote session closed for " + peer.DeviceName); }
    }

    internal void Remove(RemoteDesktopSession session)
    {
        lock (_gate) _sessions.Remove(session);
        NotifySessionsChanged();
    }

    internal void NotifySessionsChanged() =>
        _dispatcher.BeginInvoke(() => SessionsChanged?.Invoke(this, EventArgs.Empty));

    /// <summary>Ends every session, for the switch on the companion page.</summary>
    public void DisconnectAll()
    {
        RemoteDesktopSession[] sessions;
        lock (_gate) sessions = _sessions.ToArray();
        foreach (RemoteDesktopSession session in sessions) session.Stop();
    }

    // ---- Consent -----------------------------------------------------------------

    /// <summary>
    /// Decides whether this phone may have the screen.
    /// </summary>
    /// <remarks>
    /// Three ways to yes, in order of how deliberate they are: the machine is configured
    /// for unattended access, this particular phone was remembered from a previous
    /// prompt, or somebody at the desktop says so now. Everything else is no, including
    /// a prompt nobody answers.
    /// </remarks>
    internal async Task<RemoteConsentDecision> RequestConsentAsync(RemotePeer peer, CancellationToken cancellationToken)
    {
        if (RemoteDesktopSettings.Unattended) return new RemoteConsentDecision(true, false, null);
        lock (_gate)
        {
            if (_remembered.Contains(peer.DeviceId)) return new RemoteConsentDecision(true, false, null);
        }

        int seconds = RemoteDesktopSettings.ConsentSeconds;
        try
        {
            RemoteConsentAnswer answer = await _dispatcher.InvokeAsync(async () =>
            {
                var window = new RemoteConsentWindow(peer.DeviceName, peer.Address, seconds);
                window.Show();
                return await window.Answer;
            }, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();

            if (answer.Remember && answer.Allowed) Remember(peer.DeviceId);

            return answer.Allowed
                ? new RemoteConsentDecision(true, answer.ViewOnly, null)
                : new RemoteConsentDecision(false, false, "Refused on the PC.");
        }
        catch (OperationCanceledException)
        {
            return new RemoteConsentDecision(false, false, "The request was cancelled.");
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote consent: " + ex.Message);
            return new RemoteConsentDecision(false, false, "This PC could not ask for permission.");
        }
    }

    private void Remember(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        lock (_gate)
        {
            if (!_remembered.Add(deviceId)) return;
            AppConfig.Set(RememberedKey, string.Join(',', _remembered));
        }
    }

    /// <summary>Makes every remembered phone ask again next time.</summary>
    public void ForgetTrustedDevices()
    {
        lock (_gate)
        {
            if (_remembered.Count == 0) return;
            _remembered.Clear();
            AppConfig.Set(RememberedKey, string.Empty);
        }
        NotifySessionsChanged();
    }

    public int TrustedDeviceCount
    {
        get { lock (_gate) return _remembered.Count; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectAll();
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
