using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.Helpers;
using Arsenal.UI.Services;
using Arsenal.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arsenal.UI.Services.Remote;

/// <summary>
/// Local-only HTTPS bridge for the mobile companion. The Android client pins this
/// service's certificate after a six-digit pairing ceremony and stores its bearer
/// credential in Android Keystore. The bridge never exposes Arsenal directly to the
/// internet and every command is dispatched through the same typed services as WPF.
/// </summary>
public sealed class RemoteCompanionService : IDisposable
{
    public const int DefaultPort = 51117;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IServiceProvider _services;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _devicesGate = new();
    private readonly object _slashSettingsGate = new();
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly List<CompanionDeviceRecord> _devices;
    private TcpListener? _listener;
    private bool _disposed;
    private bool _subscribedToChanges;
    private bool _subscribedToNetwork;
    private IReadOnlyList<string> _addresses = Array.Empty<string>();
    private IReadOnlyList<(IPAddress Address, int PrefixLength)> _localNetworks = Array.Empty<(IPAddress, int)>();
    private IReadOnlyList<string> _hostNames = Array.Empty<string>();
    private UdpClient? _discoverySocket;
    private X509Certificate2? _certificate;
    private Task? _acceptLoop;
    private Task? _discoveryLoop;
    /// <summary>
    /// When this bridge will accept a pairing attempt, and for how many wrong answers.
    /// </summary>
    /// <remarks>
    /// The rules live in <see cref="PairingWindow"/> rather than here so they can be
    /// tested without a socket, a certificate or a phone - see Arsenal.Tests. What was
    /// here before was a permanently open endpoint with no attempt counter, which made a
    /// six-digit code guessable by anyone on the network.
    /// </remarks>
    private readonly PairingWindow _pairing = new();
    private SlashSettings? _cachedSlashSettings;
    private long _slashSettingsReadAt;
    private object? _lastSnapshot;
    private ScreenStatusSnapshot? _displayStatus;
    private volatile bool _gpuSwitching;
    private int _remoteGpuCommandPending;
    private bool _customFansSupported;
    private bool _midFanSupported;

    public bool IsRunning => _listener is not null;
    public string Address { get; private set; } = "Unavailable";

    /// <summary>
    /// The current pairing code, and the act of asking for it.
    /// </summary>
    /// <remarks>
    /// Only the pairing screen reads this, and it re-reads on a tick while it is on
    /// screen, so reading it is the signal that somebody is actually trying to pair.
    /// Each read opens or extends the pairing window. Nothing on the network path calls
    /// this - an incoming request must never be able to bring a code into existence.
    /// </remarks>
    public string PairingCode => _pairing.Peek();

    /// <summary>True while the bridge will accept a pairing attempt at all.</summary>
    public bool IsPairingOpen => _pairing.IsOpen;
    public string CertificateFingerprint => _certificate is null ? string.Empty : Convert.ToHexString(SHA256.HashData(_certificate.RawData));
    public string FingerprintDisplay => CertificateFingerprint.Length < 16 ? CertificateFingerprint : string.Join(":", Enumerable.Range(0, 8).Select(i => CertificateFingerprint.Substring(i * 2, 2))) + ":…";

    /// <summary>
    /// The whole fingerprint, in byte pairs, eight to a line.
    /// </summary>
    /// <remarks>
    /// A phone that pairs by scanning the QR code verifies this identity itself. A phone
    /// that pairs by typing the address cannot, so it shows what it was handed and asks
    /// the person to compare - which needs something on this screen to compare against,
    /// and the truncated <see cref="FingerprintDisplay"/> is not it.
    ///
    /// <para>Grouped the same way the phone groups it, so the two read side by side
    /// without having to be re-aligned by eye.</para>
    /// </remarks>
    public string FingerprintBlock
    {
        get
        {
            string fingerprint = CertificateFingerprint;
            if (fingerprint.Length < 2) return string.Empty;

            var pairs = Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)).ToArray();
            return string.Join(Environment.NewLine, pairs.Chunk(8).Select(row => string.Join(":", row)));
        }
    }
    public string PairingUri
    {
        get
        {
            string host = Address.Split(':', 2)[0];

            // Everything else this laptop answers on travels with the code. A phone that
            // cannot reach the one address printed on screen - the ordinary case when the
            // laptop holds Ethernet, Wi-Fi and a virtual adapter at once - has the rest
            // to try instead of failing the pairing outright.
            string alternates = string.Join(",", Candidates().Where(candidate => candidate != host).Take(6));
            string uri = $"arsenal://pair?host={Uri.EscapeDataString(host)}&port={DefaultPort}&code={PairingCode}&fingerprint={CertificateFingerprint}";
            return alternates.Length == 0 ? uri : uri + "&alt=" + Uri.EscapeDataString(alternates);
        }
    }

    /// <summary>
    /// Every address and name this laptop can currently be reached on, best first. The
    /// phone keeps the whole list and resolves against it, so the bridge moving between
    /// networks does not invalidate a pairing.
    /// </summary>
    public IReadOnlyList<string> Candidates() => _addresses.Concat(_hostNames).ToArray();
    public IReadOnlyList<CompanionDeviceInfo> PairedDevices
    {
        get
        {
            lock (_devicesGate)
                return _devices.OrderByDescending(device => device.LastSeenUtc ?? device.PairedUtc)
                    .Select(device => new CompanionDeviceInfo(device.Id, device.Name, device.PairedUtc, device.LastSeenUtc, device.LastAddress))
                    .ToArray();
        }
    }
    public event EventHandler? DevicesChanged;

    public RemoteCompanionService(IServiceProvider services)
    {
        _services = services;
        _devices = LoadDevices();
    }

    public void Start()
    {
        if (_listener is not null) return;
        if (!CompanionFirewall.IsAllowed)
        {
            Address = "Network access not allowed";
            Logger.WriteLine("Companion bridge waiting for private-network permission");
            return;
        }
        try
        {
            _certificate = LoadOrCreateCertificate();
            EnsureServerIdentity();

            // Deliberately not opening pairing here. The bridge starts with every logon
            // and runs for the life of the session; a code minted at startup would be a
            // live target for as long as the machine is on, for the sake of a screen
            // nobody is looking at. The first read from the pairing screen creates one.
            CompanionFirewall.UpgradeRulesIfPossible();
            _listener = CreateListener();
            _listener.Start(16);
            RefreshAddresses();
            _acceptLoop = AcceptLoopAsync(_stopping.Token);
            SubscribeToDesktopChanges();
            SubscribeToNetworkChanges();
            StartDiscovery();
            Logger.WriteLine($"Companion bridge listening on https://{Address}");
        }
        catch (Exception ex)
        {
            Address = "Could not start";
            Logger.WriteLine("Companion bridge: " + ex.Message);
        }
    }

    /// <summary>
    /// Wakes every waiting phone whenever the desktop changes something itself.
    /// </summary>
    /// <remarks>
    /// Without this the phone only learned about a change when its own poll happened to
    /// come round, so a mode switched on the PC sat stale on the phone for as long as two
    /// seconds - and a switch that took longer than the poll reported the old value first
    /// and kept it. These are the same events the desktop UI redraws itself from, so the
    /// phone now follows exactly what the window does.
    /// </remarks>
    private void SubscribeToDesktopChanges()
    {
        if (_subscribedToChanges) return;
        _subscribedToChanges = true;

        void Bump() => RemoteStateSignal.Bump();

        var performance = _services.GetRequiredService<IPerformanceService>();
        var gpu = _services.GetRequiredService<IGpuService>();
        var display = _services.GetRequiredService<IDisplayService>();
        var battery = _services.GetRequiredService<IBatteryService>();
        var lighting = _services.GetRequiredService<ILightingService>();
        var peripherals = _services.GetRequiredService<IPeripheralService>();

        // ScreenControl completed its startup probe before the companion service starts.
        // Capture that state once, then follow its normal change event. Remote snapshots
        // must never issue fresh display/ACPI capability reads on the WPF dispatcher.
        _displayStatus ??= CaptureDisplayStatus(display);
        _customFansSupported = _services.GetRequiredService<ICoolingService>().CustomFansSupported;
        _midFanSupported = Program.acpi.IsMidFanSupported();

        performance.ModeChanged += _ => Bump();
        performance.ModeLabelChanged += _ => Bump();
        gpu.GpuModeChanged += _ => Bump();
        gpu.GpuBusyChanged += (busy, _) =>
        {
            _gpuSwitching = busy;
            if (!busy) Volatile.Write(ref _remoteGpuCommandPending, 0);
            Bump();
        };
        gpu.GpuLockStatusChanged += _ => Bump();
        display.DisplayStatusChanged += snapshot =>
        {
            Volatile.Write(ref _displayStatus, snapshot);
            Bump();
        };
        display.PanelBrightnessChanged += _ => Bump();
        battery.ChargeLimitChanged += _ => Bump();
        battery.FullChargeOverrideChanged += _ => Bump();
        lighting.BrightnessChanged += _ => Bump();
        lighting.ModeChanged += _ => Bump();
        peripherals.DevicesChanged += Bump;
    }

    /// <summary>
    /// A dual-stack listener wherever the OS allows one, falling back to IPv4.
    /// </summary>
    /// <remarks>
    /// Home networks that route between two subnets very often carry working IPv6
    /// between them while the IPv4 sides stay isolated from each other, so binding IPv4
    /// only threw away the transport most likely to reach a phone on the far side of the
    /// router. IPv4 clients still arrive on this socket, as mapped addresses.
    /// </remarks>
    private static TcpListener CreateListener()
    {
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var dualStack = new TcpListener(IPAddress.IPv6Any, DefaultPort);
                dualStack.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                return dualStack;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Companion bridge could not bind dual-stack: " + ex.Message);
            }
        }
        return new TcpListener(IPAddress.Any, DefaultPort);
    }

    private void SubscribeToNetworkChanges()
    {
        if (_subscribedToNetwork) return;
        _subscribedToNetwork = true;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>
    /// Follows this laptop as it moves between networks.
    /// </summary>
    /// <remarks>
    /// A phone learns the new addresses through the snapshot it is already holding open,
    /// so a laptop that switches from Wi-Fi to Ethernet - or moves to another network
    /// entirely - is found again on the connection the phone still has, rather than
    /// after it fails on the address it was paired on.
    /// </remarks>
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        RefreshAddresses();
        RemoteStateSignal.Bump();
    }

    private void RefreshAddresses()
    {
        _addresses = LocalAddresses();
        _localNetworks = LocalNetworks();
        _hostNames = LocalHostNames();
        Address = (_addresses.FirstOrDefault() ?? "127.0.0.1") + ":" + DefaultPort;
    }

    private void StartDiscovery()
    {
        try
        {
            _discoverySocket = new UdpClient(new IPEndPoint(IPAddress.Any, DefaultPort))
            {
                EnableBroadcast = true
            };
            _discoveryLoop = DiscoveryLoopAsync(_stopping.Token);
            Logger.WriteLine($"Companion discovery listening on UDP {DefaultPort}");
        }
        catch (Exception ex)
        {
            // HTTPS pairing remains available through QR/manual entry if another
            // process or a firewall policy prevents UDP discovery.
            _discoverySocket?.Dispose();
            _discoverySocket = null;
            Logger.WriteLine("Companion discovery: " + ex.Message);
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _discoverySocket is not null)
        {
            try
            {
                UdpReceiveResult request = await _discoverySocket.ReceiveAsync(cancellationToken);
                if (!IsReachablePeer(request.RemoteEndPoint.Address)) continue;
                if (!Encoding.UTF8.GetString(request.Buffer).Equals("ARSENAL_DISCOVER_V1", StringComparison.Ordinal)) continue;

                byte[] response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    type = "arsenal-companion",
                    protocolVersion = 1,
                    deviceName = AppConfig.GetModelDisplayName(),
                    model = AppConfig.GetModelShort(),
                    port = DefaultPort,
                    serverId = AppConfig.GetString("companion_server_id"),
                    certificateSha256 = CertificateFingerprint,
                    // The phone used to take the source address of this packet as the
                    // only way back. It now gets every address and name the laptop
                    // answers on, so the route it found us by is not the only one it can
                    // use later.
                    addresses = _addresses,
                    hostNames = _hostNames
                }, JsonOptions));
                await _discoverySocket.SendAsync(response, request.RemoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Logger.WriteLine("Companion discovery request: " + ex.Message); }
        }
    }

    /// <summary>
    /// Whether a peer is near enough to be served at all.
    /// </summary>
    /// <remarks>
    /// The rule itself is <see cref="NetworkScope"/>, so it can be tested without a
    /// socket. What lives here is the machine's current answer to "which networks am I
    /// on", refreshed alongside the advertised addresses whenever the laptop moves.
    ///
    /// <para>Note that this cannot be the private-ranges test on its own:
    /// <see cref="IsAdvertisableAddress"/> hands out global IPv6 addresses, because on an
    /// IPv6 network that is what the phone in the next room has to dial.</para>
    /// </remarks>
    private bool IsReachablePeer(IPAddress? address) => NetworkScope.IsReachablePeer(address, _localNetworks);

    private static IReadOnlyList<(IPAddress Address, int PrefixLength)> LocalNetworks()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Where(unicast => unicast.PrefixLength > 0 && !IPAddress.IsLoopback(unicast.Address))
                .Select(unicast => (unicast.Address, unicast.PrefixLength))
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Companion local networks: " + ex.Message);
            return Array.Empty<(IPAddress, int)>();
        }
    }

    /// <summary>
    /// Issues a new code and opens the pairing window. Called by the pairing screen's
    /// refresh button and whenever the bridge starts.
    /// </summary>
    public void RegeneratePairingCode() => _pairing.Reissue();

    /// <summary>
    /// Closes the pairing window immediately and discards the code.
    /// </summary>
    /// <remarks>
    /// Called when the pairing screen goes away, so the window does not stay open for
    /// its full duration behind a closed page, and after too many failed attempts.
    /// </remarks>
    public void ClosePairing() => _pairing.Close();

    public void RevokeAllDevices()
    {
        lock (_devicesGate)
        {
            _devices.Clear();
            SaveDevices();
        }
        WriteToken(NewToken());

        // Revoking is a security action, so it closes pairing rather than opening a
        // fresh window: whoever is re-pairing should have to ask for that deliberately.
        ClosePairing();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RevokeDevice(string deviceId)
    {
        lock (_devicesGate)
        {
            _devices.RemoveAll(device => string.Equals(device.Id, deviceId, StringComparison.Ordinal));
            SaveDevices();
        }
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// How many connections may be in flight at once, and how long one gets to say what
    /// it wants before it is dropped.
    /// </summary>
    /// <remarks>
    /// Neither existed. A connection was accepted, handed to a task, and then waited on
    /// forever: the only cancellation reaching the handshake and the header read was the
    /// one raised when Arsenal shuts down. Anything on the network could open sockets,
    /// send nothing, and keep a TcpClient, an SslStream and a task alive in a process that
    /// already holds a quarter of a gigabyte - no credential and no handshake required.
    ///
    /// <para>The deadline covers the handshake and the request only. Routing is excluded
    /// on purpose: a snapshot request parks for <see cref="LongPollTimeout"/> by design,
    /// and that is a phone waiting to be told something, not a phone failing to speak.</para>
    /// </remarks>
    private const int MaxConnectionsInFlight = 32;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResponseDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectionSlotWait = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _connections = new(MaxConnectionsInFlight, MaxConnectionsInFlight);
    private long _lastRefusalLoggedAt;

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;

                // Address first: refusing here costs one accept, where refusing after the
                // handshake costs a certificate operation per attempt.
                IPAddress? remote = RemoteAddress(client);
                if (remote is null || !IsReachablePeer(remote))
                {
                    LogRefusal($"Companion bridge refused a connection from {remote?.ToString() ?? "an unknown address"}");
                    client.Dispose();
                    continue;
                }

                // A burst of realtime slider requests is allowed to queue briefly; a flood
                // that does not drain is turned away rather than accumulating.
                if (!await _connections.WaitAsync(ConnectionSlotWait, cancellationToken))
                {
                    LogRefusal($"Companion bridge refused a connection from {remote}: {MaxConnectionsInFlight} already in flight");
                    client.Dispose();
                    continue;
                }

                // The full connection path is asynchronous already. Avoid scheduling
                // another thread-pool work item for every phone poll and slider point.
                _ = HandleClientAsync(client, remote, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Logger.WriteLine("Companion accept: " + ex.Message); }
        }
    }

    /// <summary>The peer's address, as the phone would recognise its own.</summary>
    /// <remarks>A dual-stack socket reports IPv4 clients as ::ffff:a.b.c.d.</remarks>
    private static IPAddress? RemoteAddress(TcpClient client)
    {
        try
        {
            IPAddress? remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            return remote is not null && remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Companion peer address: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Logs a refused connection at most once a minute.
    /// </summary>
    /// <remarks>
    /// A port scanner, or anything else knocking repeatedly, would otherwise be able to
    /// fill the log file through an endpoint that answers nobody.
    /// </remarks>
    private void LogRefusal(string message)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long last = Interlocked.Read(ref _lastRefusalLoggedAt);
        if (now - last < 60) return;
        if (Interlocked.CompareExchange(ref _lastRefusalLoggedAt, now, last) != last) return;
        Logger.WriteLine(message);
    }

    private async Task HandleClientAsync(TcpClient client, IPAddress remote, CancellationToken cancellationToken)
    {
        // Retire an older idle release before TLS, routing, hardware access or media
        // setup starts. Scheduling only in finally left a narrow window where cleanup
        // from the preceding request could run underneath this one.
        Services.BackgroundMemoryRelease.NotifyActivity();

        using (client)
        using (var ssl = new SslStream(client.GetStream(), false))
        {
            try
            {
                HttpRequest request;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(RequestDeadline);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false
                    }, deadline.Token);
                    request = await ReadRequestAsync(ssl, deadline.Token);
                }

                request = request with { RemoteAddress = remote.ToString() };

                // A remote control session leaves HTTP behind and keeps this socket for
                // as long as somebody is watching, so it is handled before routing: the
                // router's contract is one response and then a closed connection, which
                // is the opposite of what a session needs.
                if (await TryUpgradeToSessionAsync(ssl, request, remote, cancellationToken)) return;

                HttpResponse response = await RouteAsync(request);
                await WriteWithDeadlineAsync(ssl, response, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Companion request: " + ex.Message);
                try { await WriteWithDeadlineAsync(ssl, Error(500, "The Windows companion could not complete this request."), cancellationToken); }
                catch { }
            }
            finally
            {
                _connections.Release();

                // The phone reaches most of the application from here - view models get
                // built, hardware gets read, snapshots get serialised - and it does all of
                // that with no window open, so nothing else was ever going to collect it.
                // Debounced, so a phone polling steadily schedules one release after it
                // stops rather than one per request.
                Services.BackgroundMemoryRelease.Schedule();
            }
        }
    }

    /// <summary>
    /// Hands the connection to a remote control session when the phone asked for one.
    /// </summary>
    /// <remarks>
    /// The upgrade is answered on the same TLS connection the phone already pinned and
    /// only after the bearer token has been checked, so a session inherits exactly the
    /// trust the rest of the companion runs on rather than establishing its own.
    ///
    /// <para>Nothing may follow the request headers: the phone waits for the 101 before
    /// it sends its first frame. A request that arrives with a body would leave those
    /// bytes in the HTTP reader's buffer where the session would never see them, so it
    /// is refused rather than silently desynchronised.</para>
    /// </remarks>
    private async Task<bool> TryUpgradeToSessionAsync(Stream stream, HttpRequest request, IPAddress remote, CancellationToken cancellationToken)
    {
        if (request.Method != "GET" || !request.Path.StartsWith(Desktop.RemoteDesktopProtocol.SessionPath, StringComparison.Ordinal)) return false;
        if (!request.Headers.TryGetValue("upgrade", out string? upgrade)
            || !string.Equals(upgrade.Trim(), Desktop.RemoteDesktopProtocol.UpgradeToken, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsAuthorized(request, out CompanionDeviceInfo? paired) || paired is null)
        {
            await WriteWithDeadlineAsync(stream, Error(401, "This phone is not paired with Arsenal."), cancellationToken);
            return true;
        }
        if (request.Body.Length > 0)
        {
            await WriteWithDeadlineAsync(stream, Error(400, "A session request cannot carry a body."), cancellationToken);
            return true;
        }

        var desktop = _services.GetRequiredService<Desktop.RemoteDesktopServer>();
        if (!desktop.IsEnabled)
        {
            await WriteWithDeadlineAsync(stream, Error(403, "Remote control is turned off on this PC."), cancellationToken);
            return true;
        }

        byte[] accepted = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: " + Desktop.RemoteDesktopProtocol.UpgradeToken + "\r\n" +
            "Connection: Upgrade\r\n\r\n");
        await stream.WriteAsync(accepted, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        await desktop.AcceptAsync(stream, new Desktop.RemotePeer(paired.Id, paired.Name, remote.ToString()));
        return true;
    }

    /// <summary>
    /// Writes a response, giving up on a client that has stopped reading.
    /// </summary>
    private static async Task WriteWithDeadlineAsync(Stream stream, HttpResponse response, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ResponseDeadline);
        await WriteResponseAsync(stream, response, deadline.Token);
    }

    /// <summary>
    /// How long a waiting phone is held before answering anyway. Short enough that a
    /// dropped connection is noticed promptly, long enough that an idle desktop is not
    /// answering requests all day.
    /// </summary>
    private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Reads ?since=N from the request path, when the phone sent one.</summary>
    private static bool TryReadVersion(HttpRequest request, out int version)
    {
        version = 0;
        int query = request.Path.IndexOf("?since=", StringComparison.Ordinal);
        return query >= 0 && int.TryParse(request.Path[(query + 7)..], out version);
    }

    private async Task<HttpResponse> RouteAsync(HttpRequest request)
    {
        if (request.Method == "POST" && request.Path == "/v1/pair") return Pair(request.Body, request.RemoteAddress);
        if (!IsAuthorized(request)) return Error(401, "This phone is not paired with Arsenal.");
        if (request.Method == "GET" && request.Path.StartsWith("/v1/snapshot", StringComparison.Ordinal))
        {
            // A phone that sends the version it already holds is asking to be told when
            // something changes rather than asking again on a timer. The request is held
            // open until the desktop moves, so a mode switched on the PC reaches the phone
            // in the time it takes to serialise it instead of on the next poll.
            if (TryReadVersion(request, out int since))
            {
                await RemoteStateSignal.WaitForChangeAsync(since, LongPollTimeout, _stopping.Token);
            }

            // A GPU transition owns the sensor providers while services and device handles
            // are being torn down or recreated. The last complete telemetry sample is the
            // only honest one during that interval; forcing another read merely contends
            // with the switch. At all other times, keep the phone's telemetry fresh.
            if (!_gpuSwitching)
                await Task.Run(() => _services.GetRequiredService<IDeviceStateService>().RefreshNow());

            return Json(200, await GetSnapshotAsync());
        }
        // Every setting this desktop can offer, described rather than assumed. Fetched
        // when the phone connects and again whenever the snapshot's catalogVersion moves,
        // which happens when hardware appears, disappears, or gains an option.
        if (request.Method == "GET" && request.Path.StartsWith("/v1/catalog", StringComparison.Ordinal))
        {
            return Json(200, await OnUiAsync(() => CompanionCatalog.Build(_services)));
        }
        // Battery health, parsed from powercfg rather than opened as HTML on a screen the
        // person holding the phone cannot see. Runs off the UI thread: the tool takes
        // several seconds and nothing here touches WPF.
        if (request.Method == "GET" && request.Path.StartsWith("/v1/battery-report", StringComparison.Ordinal))
        {
            return Json(200, await CompanionBatteryReport.GetAsync(_stopping.Token));
        }
        if (request.Method == "POST" && request.Path == "/v1/command")
        {
            using JsonDocument body = ParseBody(request.Body);
            string action = body.RootElement.GetProperty("action").GetString() ?? string.Empty;
            bool realtime = body.RootElement.TryGetProperty("realtime", out JsonElement realtimeValue) && Bool(realtimeValue);
            await OnUiAsync(() => DispatchCommandAsync(action, body.RootElement)).Unwrap();

            // Whatever the command changed, the desktop UI and any other phone watching
            // should hear about it rather than waiting for their own next poll.
            // GPU mode publishes its own accepted mode, busy stages and completion. An
            // extra bump here wakes the already-waiting phone before the worker has even
            // started, causing a redundant snapshot to race the hardware transition.
            if (!string.Equals(action, "gpu.mode", StringComparison.Ordinal))
                RemoteStateSignal.Bump();
            bool liveNotification = await OnUiAsync(() => ShowLiveRemoteNotification(action, body.RootElement));
            if (realtime) return Json(200, new { ok = true });
            if (!liveNotification) await OnUiAsync(() => ShowRemoteNotification(action, body.RootElement));
            return Json(200, await GetSnapshotAsync());
        }
        return Error(404, "Unknown companion endpoint.");
    }

    private HttpResponse Pair(string body, string remoteAddress)
    {
        using JsonDocument document = ParseBody(body);
        string code = document.RootElement.TryGetProperty("code", out var codeValue) ? codeValue.GetString() ?? string.Empty : string.Empty;

        // Deliberately never asks the window to issue a code. This path is reachable by
        // anyone who can route to the machine, and minting one in response to an incoming
        // request meant a guesser was creating the very credential it was guessing at.
        PairingAttempt attempt = _pairing.Submit(code);

        if (attempt == PairingAttempt.LockedOut)
        {
            Logger.WriteLine($"Companion pairing closed after {PairingWindow.MaxFailures} failed attempts from {remoteAddress}");
            return Error(403, "Too many incorrect codes. Open Mobile companion on the PC to try again.");
        }
        if (attempt != PairingAttempt.Accepted)
        {
            // One message for a wrong code and for a closed window alike, so the endpoint
            // does not tell an unauthenticated caller whether anyone is pairing right now.
            return Error(403, "The pairing code is incorrect or has expired. Open Mobile companion on the PC to start pairing.");
        }

        string deviceName = document.RootElement.TryGetProperty("deviceName", out var name) ? name.GetString() ?? "Android phone" : "Android phone";
        string deviceId = Guid.NewGuid().ToString("N");
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        lock (_devicesGate)
        {
            _devices.Add(new CompanionDeviceRecord
            {
                Id = deviceId,
                Name = deviceName.Trim().Length == 0 ? "Android phone" : deviceName.Trim(),
                Token = token,
                LastAddress = remoteAddress,
                PairedUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow
            });
            if (_devices.Count > 20)
                _devices.RemoveRange(0, _devices.Count - 20);
            SaveDevices();
        }
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return Json(200, new
        {
            token,
            deviceId,
            certificateSha256 = CertificateFingerprint,
            serverId = AppConfig.GetString("companion_server_id"),
            deviceName = AppConfig.GetModelDisplayName(),
            // A phone that paired by typing one address in by hand still leaves knowing
            // every other way back to this laptop.
            addresses = _addresses,
            hostNames = _hostNames
        });
    }

    private bool IsAuthorized(HttpRequest request) => IsAuthorized(request, out _);

    /// <summary>
    /// Checks the bearer credential and says which paired phone presented it.
    /// </summary>
    /// <remarks>
    /// Every other endpoint only needs to know that somebody paired is asking. A remote
    /// control session needs to know <i>which</i> phone, because consent is remembered
    /// per device and the prompt has to name who is asking.
    /// </remarks>
    private bool IsAuthorized(HttpRequest request, out CompanionDeviceInfo? paired)
    {
        paired = null;
        if (!request.Headers.TryGetValue("authorization", out string? authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        string presentedText = authorization[7..];
        CompanionDeviceRecord? matched = null;
        bool persist = false;
        lock (_devicesGate)
        {
            byte[] presented = Encoding.UTF8.GetBytes(presentedText);
            foreach (CompanionDeviceRecord device in _devices)
            {
                byte[] expected = device.TokenBytes;
                if (presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected))
                {
                    matched = device;
                    persist = device.LastSeenUtc is null || DateTimeOffset.UtcNow - device.LastSeenUtc > TimeSpan.FromSeconds(20);
                    device.LastSeenUtc = DateTimeOffset.UtcNow;
                    device.LastAddress = request.RemoteAddress;
                    if (persist) SaveDevices();
                    break;
                }
            }

            // One-release migration path for a phone paired by an earlier companion build.
            if (matched is null && SecureEquals(presentedText, GetToken()))
            {
                matched = new CompanionDeviceRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = AppConfig.GetString("companion_last_device") is { Length: > 0 } name ? name : "Android phone",
                    Token = presentedText,
                    LastAddress = request.RemoteAddress,
                    PairedUtc = DateTimeOffset.UtcNow,
                    LastSeenUtc = DateTimeOffset.UtcNow
                };
                _devices.Add(matched);
                SaveDevices();
                persist = true;
            }
        }
        // Realtime sliders can authorize more than ten requests per second. Presence
        // only changes on the same 20-second boundary that is persisted, while the
        // visible companion page has its own five-second refresh. Avoid allocating and
        // rebinding its complete device collection for every authenticated command.
        if (matched is not null && persist) DevicesChanged?.Invoke(this, EventArgs.Empty);
        if (matched is not null)
        {
            paired = new CompanionDeviceInfo(matched.Id, matched.Name, matched.PairedUtc, matched.LastSeenUtc, matched.LastAddress);
        }
        return matched is not null;
    }

    /// <summary>
    /// AniMe Matrix running modes, in the order of <see cref="Arsenal.AnimeMatrix.MatrixMode"/>
    /// so a list position is also the firmware value. The desktop's own Lighting page
    /// builds its radio list from exactly this set.
    /// </summary>
    private static string[] AnimeMatrixModeNames => new[]
    {
        "Banner",
        "Logo",
        "Picture",
        "Clock",
        "Audio",
        "Text",
    };

    /// <summary>One hardware bound, in the shape the companion reads it back as.</summary>
    private static object Range(Arsenal.Application.Models.ControlRange range, int step = 1) =>
        new { minimum = range.Minimum, maximum = range.Maximum, step };

    private object CreateSnapshot()
    {
        var performance = _services.GetRequiredService<IPerformanceService>();
        var gpu = _services.GetRequiredService<IGpuService>();
        var display = _services.GetRequiredService<IDisplayService>();
        var battery = _services.GetRequiredService<IBatteryService>();
        var cooling = _services.GetRequiredService<ICoolingService>();
        var lighting = _services.GetRequiredService<ILightingService>();
        var peripherals = _services.GetRequiredService<IPeripheralService>();
        var profileService = _services.GetRequiredService<IProfileService>();
        ScreenStatusSnapshot displayStatus = Volatile.Read(ref _displayStatus) ?? CaptureDisplayStatus(display);
        var slashSettings = ReadSlashSettings();
        var telemetry = _services.GetRequiredService<IDeviceStateService>().CurrentTelemetry;
        var profile = performance.GetCurrentProfile();
        var cpuFan = cooling.GetFanCurve(0, profile.ModeIndex);
        var gpuFan = cooling.GetFanCurve(1, profile.ModeIndex);
        var midFan = cooling.GetFanCurve(2, profile.ModeIndex);

        return new
        {
            serverId = AppConfig.GetString("companion_server_id"),
            // Where this laptop can be reached right now. A connected phone re-caches
            // these on every snapshot, so by the time an address it was paired on stops
            // working it already holds the ones that replaced it.
            addresses = _addresses,
            hostNames = _hostNames,
            // What the phone echoes back to be woken on the next change.
            stateVersion = RemoteStateSignal.Version,
            deviceName = AppConfig.GetModelDisplayName(),
            model = AppConfig.GetModelShort(),
            bios = AppConfig.GetBiosAndModel().Item1,
            appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
            // The colour this desktop is lit with, so the phone can light itself the same
            // way rather than carrying a palette of its own that drifts from this one.
            // Follows the Windows accent or the user's override, whichever is configured.
            accent = ToHex(AccentColorService.GetConfiguredAccent()),
            telemetry,
            // A question the desktop is waiting on, if any.
            prompt = RemoteRestartPrompt.Current,
            capabilities = new
            {
                ecoGpu = gpu.IsEcoSupported,
                mux = gpu.IsMuxSupported,
                customFans = _customFansSupported,
                midFan = _midFanSupported,
                undervolt = performance.IsUndervoltSupported,
                igpuUndervolt = performance.IsIgpuUndervoltSupported,
                miniLed = displayStatus.Miniled1 >= 0 || displayStatus.Miniled2 >= 0,
                overdrive = displayStatus.OverdriveSetting,
                resolutionToggle = displayStatus.Fhd >= 0,
                hdrControl = displayStatus.Hdr && displayStatus.HdrControl >= 0,
                oled = AppConfig.IsOLED(),
                animeMatrix = lighting.HasAnimeMatrix,
                slash = lighting.HasSlash,
                keyboardColor = lighting.HasKeyboardColor,
                auraEffects = lighting.HasAuraEffects,
                dedicatedGpu = gpu.HasDedicatedGpu,
                steppedChargeLimit = AppConfig.IsChargeLimit6080(),

                // The option lists travel with the capabilities rather than being
                // hard-coded on the phone. GameVisual identifiers are SplendidCommand
                // values - Default is 11, Vivid 13, Cinema 25 - and the valid set differs
                // between ROG and Vivobook/Zenbook panels, so a fixed list on the client
                // sent numbers that meant nothing on the machine receiving them.
                visualProfiles = VisualControl.GetVisualModes()
                    .Select(pair => new { id = (int)pair.Key, name = pair.Value }).ToArray(),
                // The desktop prefixes these with "Gamut: " because its row has no heading
                // of its own; the phone puts them under one, so the prefix only repeats it.
                gamuts = VisualControl.GetGamutModes()
                    .Select(pair => new { id = (int)pair.Key, name = pair.Value.Replace("Gamut: ", string.Empty) }).ToArray(),
                auraModes = Arsenal.USB.Aura.GetModes()
                    .Select(pair => new { id = (int)pair.Key, name = pair.Value }).ToArray(),

                // The lighting device's own running modes, for the same reason the visual
                // profiles and Aura modes travel here: these are firmware values, and a
                // list assumed on the client selects whatever happens to sit at that
                // index. Slash and AniMe are different devices with different modes -
                // twenty three against six - and neither of them is the fixed
                // System/Clock/Audio triple the phone used to draw. On a Slash chassis
                // that triple was really Bounce, Slash and Loading, so "Clock" set Slash
                // and "Audio" set Loading, and twenty modes had no way to be reached.
                matrixModes = lighting.HasSlash
                    ? Arsenal.AnimeMatrix.SlashDevice.Modes
                        .Select(pair => new { id = (int)pair.Key, name = pair.Value }).ToArray()
                    : AnimeMatrixModeNames
                        .Select((name, index) => new { id = index, name }).ToArray(),

                // Performance profiles, including any custom ones the user has added.
                // Modes.GetDictonary() is Silent/Balanced/Turbo plus every profile from
                // index three up, so a fixed three-entry list on the phone showed a
                // custom profile as whichever of the three it happened to fall through to.
                performanceModes = Arsenal.Mode.Modes.GetDictonary()
                    .Select(pair => new { id = pair.Key, name = pair.Value }).ToArray(),

                // Discrete option sets the desktop offers as lists, not as tracks. A
                // slider over them lets the thumb rest between two supported values.
                slashDimLevels = new[] { 10, 20, 30, 40, 50, 100 },
                sleepTimeouts = new[] { 0, 1, 2, 3, 5, 10 }
            },

            // Every bound the desktop draws its own sliders from.
            //
            // These are not decoration: AsusACPI narrows its statics from the chassis
            // model and NvidiaGpuControl widens its offsets once it knows the card, so a
            // power ceiling is 50 W on an Ally and 250 W on an Advantage Edition. The
            // writers clamp to the same numbers and say nothing when they do, so a phone
            // drawing a fixed track offers values that are silently dropped - the slider
            // moves, the snapshot comes back unchanged, and nothing explains why.
            ranges = new
            {
                powerLimit = Range(performance.PowerLimitRange),
                cpuTemp = Range(performance.CpuTempRange),
                cpuUndervolt = Range(performance.CpuUndervoltRange),
                igpuUndervolt = Range(performance.IgpuUndervoltRange),
                fanHysteresis = Range(performance.FanHysteresisRange),
                gpuCoreOffset = Range(gpu.GpuCoreOffsetRange),
                gpuMemoryOffset = Range(gpu.GpuMemoryOffsetRange),
                gpuBoost = Range(gpu.GpuBoostRange),
                gpuTemp = Range(gpu.GpuTempRange),
                gpuClockLimit = Range(gpu.GpuClockLimitRange, 50),
                // The adjustable offset sits on top of the card's base wattage, which is
                // what the desktop's own slider shows.
                gpuPowerTotal = new
                {
                    minimum = gpu.GpuPowerBaseWatts + gpu.GpuPowerOffsetRange.Minimum,
                    maximum = gpu.GpuPowerBaseWatts + gpu.GpuPowerOffsetRange.Maximum,
                    step = 1,
                },
                // Widened to whatever is already stored, so a value set elsewhere is
                // never off the end of the track that has to display it.
                hibernateMinutes = new { minimum = 0, maximum = Math.Max(720, Arsenal.Mode.PowerNative.GetHibernateAfter()), step = 5 },
                backlightBatterySeconds = new { minimum = 0, maximum = Math.Max(600, AppConfig.Get("keyboard_timeout", 60)), step = 15 },
                backlightAcSeconds = new { minimum = 0, maximum = Math.Max(600, AppConfig.Get("keyboard_ac_timeout", 0)), step = 15 },
                chargeLimit = new { minimum = AppConfig.IsChargeLimit6080() ? 60 : 40, maximum = 100, step = 5 },
                panelBrightness = new { minimum = 0, maximum = 100, step = 1 },
                oledDimming = new { minimum = 20, maximum = 100, step = 1 },
                slashInterval = new { minimum = 0, maximum = 5, step = 1 },
                toastDuration = new { minimum = 2, maximum = 12, step = 1 },
                remoteConsent = new { minimum = 10, maximum = 120, step = 5 },
            },

            // Moves whenever an option list, title or availability changes. The phone
            // re-fetches /v1/catalog when it sees a number it does not already hold.
            catalogVersion = CompanionCatalog.Version(_services),
            settings = new
            {
                performanceMode = performance.CurrentMode,
                gpuMode = gpu.CurrentGpuMode,
                spl = Positive(profile.Spl, 45), sppt = Positive(profile.Sppt, 65), fppt = Positive(profile.Fppt, 80),
                cpuTempLimit = Positive(profile.CpuTempLimit, 95), cpuBoost = Math.Max(0, profile.CpuBoost),
                cpuUndervolt = profile.CpuUndervolt, igpuUndervolt = profile.IgpuUndervolt,
                profile.ApplyPower, profile.ApplyFans, profile.ApplyUndervolt,
                gpuCoreOffset = profile.GpuCoreOffset, gpuMemoryOffset = profile.GpuMemoryOffset,
                gpuBoost = PositiveOrZero(profile.GpuBoost, 25), gpuTempTarget = Positive(profile.GpuTempTarget, 87),
                gpuPowerTarget = Positive(profile.GpuPowerTarget, 140), gpuClockLimit = Positive(profile.GpuClockLimit, 3000),
                fanHysteresisUp = Math.Max(0, profile.FanHysteresisUp), fanHysteresisDown = Math.Max(0, profile.FanHysteresisDown),
                cpuFanCurve = Curve(cpuFan), gpuFanCurve = Curve(gpuFan), midFanCurve = Curve(midFan),
                refreshRate = displayStatus.Frequency, maxRefreshRate = displayStatus.MaxFrequency,
                autoRefresh = displayStatus.ScreenAuto, overdrive = displayStatus.Overdrive > 0,
                miniLed = displayStatus.Miniled1 > 0 || displayStatus.Miniled2 > 0,
                panelBrightness = display.PanelBrightness, oledDimming = display.Brightness, visualProfile = display.CurrentVisualProfile,
                colorTemperature = display.ColorTemperature, gamut = display.CurrentGamut,
                chargeLimit = battery.ChargeLimit, fullChargeOverride = battery.IsFullChargeOverride,
                keyboardBrightness = lighting.Brightness, lightingMode = lighting.CurrentMode, lightingSpeed = AppConfig.Get("aura_speed", 1),
                lightingColor = AppConfig.Get("aura_color", 0x62E6B5),
                lightAwake = AppConfig.IsNotFalse("keyboard_awake"), lightBoot = AppConfig.IsNotFalse("keyboard_boot"),
                lightSleep = AppConfig.IsNotFalse("keyboard_sleep"), lightShutdown = AppConfig.IsNotFalse("keyboard_shutdown"),
                matrixBrightness = lighting.MatrixBrightness, matrixMode = lighting.MatrixMode,
                matrixOffBattery = AppConfig.Is("matrix_auto"), matrixOffLid = AppConfig.Is("matrix_lid"),
                matrixFlip = AppConfig.Is("matrix_flip"),
                autoSwitch = profileService.IsAutoSwitchEnabled, autoAcMode = profileService.AutoAcMode, autoBatteryMode = profileService.AutoBatteryMode,
                ecoOnBattery = AppConfig.Is("gpu_auto"), clamshellMode = AppConfig.Is("clamshell"),
                fnLock = AppConfig.Is("fn_lock"), statusLeds = AppConfig.IsNotFalse("status_led"),
                numberPad = AppConfig.IsNumberPad() && Arsenal.Input.NumberPad.Get() == 1,
                hardwareOverlay = AppConfig.IsOverlay(), overlayGamingOnly = AppConfig.IsOverlayGameOnly(),
                pciePowerSaving = AppConfig.IsNotFalse("aspm"), standbyNetworking = AppConfig.IsNotFalse("standby_networking"),
                optimizedOnUsbC = AppConfig.Is("optimized_usbc"), closeGpuApps = AppConfig.Is("kill_gpu_apps"),
                nvidiaPlatform = AppConfig.Is("nv_platform"),
                disableOverdriveAutomation = AppConfig.Is("no_overdrive"), forceOverdrive = AppConfig.Is("force_overdrive"),
                automaticClamshell = AppConfig.Is("toggle_clamshell_mode"), alwaysOnTop = AppConfig.Is("topmost"),
                handheldControls = AppConfig.IsAlly(), autoTdp = Arsenal.Ally.AllyControl.IsAutoTdpEnabled,
                fpsLimit = Arsenal.Ally.AllyControl.CurrentFpsLimit,
                bootSound = AppConfig.IsNotFalse("boot_sound"), hibernateMinutes = Math.Max(0, Arsenal.Mode.PowerNative.GetHibernateAfter()),
                backlightBatterySeconds = AppConfig.Get("keyboard_timeout", 60), backlightAcSeconds = AppConfig.Get("keyboard_ac_timeout", 0),
                slashInterval = slashSettings.Interval, slashBootAnimation = slashSettings.BootAnimation,
                slashSleepAnimation = slashSettings.SleepAnimation, slashSleepPattern = slashSettings.SleepPattern,
                slashLowBatteryAlert = slashSettings.LowBatteryAlert, slashBatteryIndicator = slashSettings.BatteryIndicator,
                slashPowerSaving = slashSettings.PowerSaving, slashDimLevel = slashSettings.DimLevel,
                runOnStartup = Startup.IsScheduled(), startMinimized = AppConfig.Is(ApplicationLaunch.StartMinimizedSetting),
                minimizeToTray = AppConfig.IsNotFalse("minimize_to_tray"),
                checkUpdates = AppConfig.IsNotFalse("check_updates"), theme = AppConfig.Get("theme", 0),
                toastEnabled = AppConfig.IsNotFalse("toast_enabled"), toastStyle = AppConfig.Get("toast_style", 0),
                toastPosition = AppConfig.Get("toast_position", 0), toastDuration = AppConfig.Get("toast_duration", 3500), toastProgress = AppConfig.IsNotFalse("toast_progress"),

                // Remote control. The phone needs these to draw its own switches, and
                // to know before it offers a Connect button whether connecting would
                // work at all.
                remoteEnabled = Desktop.RemoteDesktopSettings.Enabled,
                remoteUnattended = Desktop.RemoteDesktopSettings.Unattended,
                remoteInput = Desktop.RemoteDesktopSettings.AllowInput,
                remoteAudio = Desktop.RemoteDesktopSettings.AllowAudio,
                remoteClipboard = Desktop.RemoteDesktopSettings.AllowClipboard,
                remoteFiles = Desktop.RemoteDesktopSettings.AllowFiles,
                remoteLockOnDisconnect = Desktop.RemoteDesktopSettings.LockOnDisconnect,
                remoteConsentSeconds = Desktop.RemoteDesktopSettings.ConsentSeconds,
                touchpadEnabled = _services.GetRequiredService<IInputDeviceService>().IsTouchpadEnabled
            },
            remote = new
            {
                enabled = Desktop.RemoteDesktopSettings.Enabled,
                sessions = _services.GetRequiredService<Desktop.RemoteDesktopServer>().ActiveSessions,
                status = _services.GetRequiredService<Desktop.RemoteDesktopServer>().StatusLine,
                port = DefaultPort,
                path = Desktop.RemoteDesktopProtocol.SessionPath,
                protocol = Desktop.RemoteDesktopProtocol.UpgradeToken,
                monitors = Desktop.RemoteMonitors.Enumerate()
                    .Select(monitor => new { index = monitor.Index, name = monitor.Name, width = monitor.Width, height = monitor.Height, primary = monitor.Primary })
                    .ToArray(),
            },
            peripherals = peripherals.Devices.Select(device => new
            {
                id = device.Id, name = device.Name, type = device.DeviceType, battery = device.BatteryPercentage,
                dpi = device.CurrentDpi, pollingRate = device.PollingRate, sleepMinutes = device.SleepMinutes,

                // What this sensor will actually take. Mice differ by an order of
                // magnitude - a TUF M3 stops at 2000 where an Aimpoint reaches 36000 -
                // and the service clamps writes to these same bounds, so a track drawn
                // from anything else is drawing a lie.
                minDpi = device.MinDpi, maxDpi = device.MaxDpi, dpiStep = device.DpiStep,

                // The rates this mouse reports at. The series doubles rather than
                // stepping evenly, so they travel as explicit stops.
                pollingRates = device.PollingRates is { Count: > 0 }
                    ? device.PollingRates.ToArray()
                    : new[] { device.PollingRate },
            }).ToArray()
        };
    }

    /// <summary>
    /// Coalesces command-response and long-poll snapshots and constructs the result away
    /// from WPF. Android keeps both requests open at once, so a single state change used to
    /// run this hardware-heavy projection twice on the UI dispatcher. If another request
    /// refreshed it while this caller waited for the gate, that completed result is reused.
    /// </summary>
    private async Task<object> GetSnapshotAsync()
    {
        object? observed = Volatile.Read(ref _lastSnapshot);
        await _snapshotGate.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            object? completedWhileWaiting = Volatile.Read(ref _lastSnapshot);
            if (completedWhileWaiting is not null && !ReferenceEquals(observed, completedWhileWaiting))
                return completedWhileWaiting;

            object snapshot = await Task.Run(CreateSnapshot, _stopping.Token).ConfigureAwait(false);
            Volatile.Write(ref _lastSnapshot, snapshot);
            return snapshot;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    /// <summary>
    /// Seeds the remote display cache after ScreenControl's startup probe. Subsequent
    /// ScreenStatusSnapshot events replace it atomically, so phone reads are current while
    /// never re-querying WMI/ACPI on the desktop dispatcher.
    /// </summary>
    private static ScreenStatusSnapshot CaptureDisplayStatus(IDisplayService display)
    {
        bool miniLedSupported = display.IsMiniLedSupported;
        int miniLed = miniLedSupported ? display.MiniLedMode : -1;
        bool hdr = display.IsHdrEnabled;

        return new ScreenStatusSnapshot(
            // CurrentRefreshRate reads the cached rate and is always >= 0, so it can
            // never report the panel as off. Ask the display stack instead.
            ScreenEnabled: display.IsInternalPanelActive,
            ScreenAuto: display.IsAutoRefreshEnabled,
            Frequency: display.CurrentRefreshRate,
            MaxFrequency: display.MaxRefreshRate,
            Overdrive: display.IsOverdriveEnabled ? 1 : 0,
            OverdriveSetting: display.IsOverdriveAvailable,
            Miniled1: miniLed,
            Miniled2: -1,
            Hdr: hdr,
            Acm: display.IsAcmEnabled,
            Fhd: display.IsResolutionToggleSupported ? 0 : -1,
            HdrControl: hdr && display.IsHdrControlSupported ? 0 : -1);
    }

    private readonly record struct SlashSettings(
        int Interval,
        bool BootAnimation,
        bool SleepAnimation,
        int SleepPattern,
        bool LowBatteryAlert,
        bool BatteryIndicator,
        bool PowerSaving,
        int DimLevel);

    /// <summary>
    /// Remote status should not construct the complete Lighting page (including its
    /// option collections and event subscriptions). Read the same persisted/device
    /// values that LightingViewModel loads when that page is actually opened.
    /// </summary>
    private SlashSettings ReadSlashSettings()
    {
        lock (_slashSettingsGate)
        {
            long now = Environment.TickCount64;
            if (_cachedSlashSettings.HasValue && now - _slashSettingsReadAt < 5000)
                return _cachedSlashSettings.Value;

            int interval = Math.Clamp(AppConfig.Get("matrix_interval", 0), 0, 5);
            int dimLevel = AppConfig.Get("slash_dim", 20);
            bool bootAnimation = false;
            bool sleepAnimation = false;
            int sleepPattern = 0;
            bool lowBatteryAlert = false;
            bool batteryIndicator = false;
            bool powerSaving = false;

            if (Program.matrixControl?.deviceSlash is { } slash)
            {
                try
                {
                    bootAnimation = slash.GetFlag(0xA0);
                    byte[]? sleep = slash.GetRecord(0xA1);
                    sleepAnimation = sleep is not null && sleep[8] == 0x01;
                    sleepPattern = sleep is not null && sleep[6] != 0x00 ? 1 : 0;
                    lowBatteryAlert = slash.GetFlag(0xA2);
                    batteryIndicator = slash.GetFlag(0xA3);
                    powerSaving = slash.GetFlag(0xA8);
                }
                catch { }
            }

            _cachedSlashSettings = new SlashSettings(interval, bootAnimation, sleepAnimation, sleepPattern,
                lowBatteryAlert, batteryIndicator, powerSaving, dimLevel);
            _slashSettingsReadAt = now;
            return _cachedSlashSettings.Value;
        }
    }

    private async Task DispatchCommandAsync(string action, JsonElement request)
    {
        // Marks everything below as phone-originated, so a confirmation the desktop would
        // normally raise as a modal is asked of the phone instead.
        using var companionScope = RemoteRestartPrompt.CommandScope();

        var performance = _services.GetRequiredService<IPerformanceService>();
        var gpu = _services.GetRequiredService<IGpuService>();
        var display = _services.GetRequiredService<IDisplayService>();
        var battery = _services.GetRequiredService<IBatteryService>();
        var cooling = _services.GetRequiredService<ICoolingService>();
        var lighting = _services.GetRequiredService<ILightingService>();
        var peripherals = _services.GetRequiredService<IPeripheralService>();
        var automation = _services.GetRequiredService<IProfileService>();
        JsonElement value = request.TryGetProperty("value", out var v) ? v : default;
        JsonElement values = request.TryGetProperty("values", out var vs) ? vs : default;

        if (action.StartsWith("performance.", StringComparison.Ordinal))
        {
            if (action == "performance.mode") { performance.SetMode(Int(value)); return; }
            if (action == "performance.reset") { performance.ResetProfile(performance.CurrentMode); return; }
            if (action == "performance.save") { performance.SaveProfile(performance.GetCurrentProfile()); return; }
            PerformanceProfile p = performance.GetCurrentProfile();
            switch (action)
            {
                case "performance.spl": p.Spl = Int(value); break;
                case "performance.sppt": p.Sppt = Int(value); break;
                case "performance.fppt": p.Fppt = Int(value); break;
                case "performance.cpuTemp": p.CpuTempLimit = Int(value); break;
                case "performance.cpuBoost": p.CpuBoost = Int(value); break;
                case "performance.cpuUndervolt": p.CpuUndervolt = Int(value); break;
                case "performance.igpuUndervolt": p.IgpuUndervolt = Int(value); break;
                case "performance.applyPower": p.ApplyPower = Bool(value); break;
                case "performance.applyFans": p.ApplyFans = Bool(value); break;
                case "performance.applyUndervolt": p.ApplyUndervolt = Bool(value); break;
                case "performance.gpuCore": p.GpuCoreOffset = Int(value); break;
                case "performance.gpuMemory": p.GpuMemoryOffset = Int(value); break;
                case "performance.gpuBoost": p.GpuBoost = Int(value); break;
                case "performance.gpuTemp": p.GpuTempTarget = Int(value); break;
                case "performance.gpuPower": p.GpuPowerTarget = Int(value); break;
                case "performance.gpuClock": p.GpuClockLimit = Int(value); break;
                case "performance.fanHysteresisUp": p.FanHysteresisUp = Int(value); break;
                case "performance.fanHysteresisDown": p.FanHysteresisDown = Int(value); break;
                default: throw new InvalidOperationException("Unknown performance command.");
            }
            performance.SaveProfile(p);
            return;
        }

        switch (action)
        {
            // Crossing into or out of Ultimate raises a restart confirmation, and when the
            // phone asked for the switch it is the phone that has to answer it. That answer
            // arrives as a separate companion command, and the question itself only reaches
            // the phone through a snapshot, so this can neither hold the dispatcher - which
            // both of those need - nor hold this request open until it is answered, which
            // outlasts the client's read timeout several times over. Between them those two
            // are what froze the window and left the switch abandoned.
            //
            // So the switch is started and let go. It is a long-running operation either
            // way - the Eco and Standard paths have always returned before the hardware
            // has finished - and its progress already reaches the phone through the state
            // signal. The companion scope is captured by the worker, so the confirmation
            // underneath it still knows to ask the phone.
            case "gpu.mode":
                int gpuMode = Int(value);
                if (!QueueRemoteGpuMode(gpu, gpuMode))
                    Logger.WriteLine("Companion GPU switch ignored: a switch is already in progress");
                break;
            case "gpu.killApps": gpu.KillGpuApps(); break;
            case "gpu.restartServices": gpu.RestartNvServices(); break;
            case "gpu.toggleXgm": gpu.ToggleXgm(); break;
            case "fans.cpuCurve": SaveCurve(cooling, 0, performance.CurrentMode, values); break;
            case "fans.gpuCurve": SaveCurve(cooling, 1, performance.CurrentMode, values); break;
            case "fans.midCurve": SaveCurve(cooling, 2, performance.CurrentMode, values); break;
            case "fans.apply": cooling.ApplyFanCurves(performance.CurrentMode); break;
            case "fans.calibrate": cooling.StartCalibration(); break;
            case "display.refresh": display.SetRefreshRate(Int(value) > Arsenal.Display.ScreenControl.MIN_RATE ? Arsenal.Display.ScreenControl.MAX_REFRESH : Arsenal.Display.ScreenControl.MIN_RATE); break;
            case "display.autoRefresh": display.SetAutoRefresh(Bool(value)); break;
            case "display.overdrive": display.SetOverdrive(Bool(value)); break;
            case "display.miniLed": if ((display.MiniLedMode > 0) != Bool(value)) display.ToggleMiniLed(); break;
            case "display.panelBrightness": display.SetPanelBrightness(Int(value)); break;
            case "display.oledDimming": display.SetBrightness(Int(value)); break;
            case "display.visualProfile": display.SetVisualProfile(Int(value)); break;
            case "display.temperature": display.SetColorTemperature(Int(value)); break;
            case "display.gamut": display.SetGamut(Int(value)); break;
            case "display.resolution": display.ToggleResolution(); break;
            case "display.hdr": display.ToggleHdrControl(); break;
            case "display.touch": display.ToggleTouchScreen(); break;
            case "display.installProfiles": await display.InstallColorProfilesAsync(); break;
            case "battery.limit": battery.SetChargeLimit(Int(value)); break;
            case "battery.fullCharge": if (battery.IsFullChargeOverride != Bool(value)) battery.ToggleFullChargeOverride(); break;
            case "battery.report": battery.GenerateBatteryReport(); break;
            case "lighting.brightness": lighting.SetBrightness(Int(value)); break;
            case "lighting.mode": lighting.SetMode(Int(value)); break;
            case "lighting.speed": lighting.SetSpeed(Int(value)); break;
            case "lighting.color": int rgb = Int(value); lighting.SetColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); break;
            case "lighting.awake": lighting.SetAwake(Bool(value)); break;
            case "lighting.boot": lighting.SetBoot(Bool(value)); break;
            case "lighting.sleep": lighting.SetSleep(Bool(value)); break;
            case "lighting.shutdown": lighting.SetShutdown(Bool(value)); break;
            case "matrix.brightness": lighting.SetMatrixBrightness(Int(value)); break;
            case "matrix.mode": lighting.SetMatrixMode(Int(value)); break;
            case "matrix.offBattery": lighting.SetMatrixPowerPolicy(Bool(value), AppConfig.Is("matrix_lid")); break;
            case "matrix.offLid": lighting.SetMatrixPowerPolicy(AppConfig.Is("matrix_auto"), Bool(value)); break;
            // Rotation has no service method: the Lighting page writes the setting and
            // re-presents the panel, and this does the same rather than inventing a
            // second path to the same two lines.
            case "matrix.flip":
                AppConfig.Set("matrix_flip", Bool(value) ? 1 : 0);
                Program.matrixControl?.deviceMatrix?.PresentClock();
                break;
            case "slash.interval": SetLighting(vm => vm.SlashInterval = Int(value)); break;
            case "slash.boot": SetLighting(vm => vm.SlashBootAnimation = Bool(value)); break;
            case "slash.sleep": SetLighting(vm => vm.SlashSleepAnimation = Bool(value)); break;
            case "slash.sleepPattern": SetLighting(vm => vm.SlashSleepPattern = Int(value)); break;
            case "slash.lowBattery": SetLighting(vm => vm.SlashLowBatteryAlert = Bool(value)); break;
            case "slash.batteryIndicator": SetLighting(vm => vm.SlashBatteryIndicator = Bool(value)); break;
            case "slash.powerSaving": SetLighting(vm => vm.SlashPowerSaving = Bool(value)); break;
            case "slash.dimLevel": SetLighting(vm => vm.SlashDimLevel = Int(value)); break;
            case "automation.enabled": automation.IsAutoSwitchEnabled = Bool(value); break;
            case "automation.acMode": automation.AutoAcMode = Int(value); break;
            case "automation.batteryMode": automation.AutoBatteryMode = Int(value); break;
            case "automation.ecoOnBattery": AppConfig.Set("gpu_auto", Bool(value) ? 1 : 0); break;
            case "automation.clamshell": SetAutomationClamshell(Bool(value)); break;
            case "peripherals.refresh": peripherals.RefreshDevices(); break;
            case "peripherals.dpi": peripherals.SetDpi(String(values, "deviceId"), Int(values, "value")); break;
            case "peripherals.polling": peripherals.SetPollingRate(String(values, "deviceId"), Int(values, "value")); break;
            case "peripherals.sleep": peripherals.SetSleepTimeout(String(values, "deviceId"), Int(values, "value")); break;

            // The rest of what a supported ASUS keyboard or mouse exposes. These were
            // reachable only from the Windows Devices page, so a phone could change a
            // mouse's DPI but not the lighting on the keyboard sitting beside it.
            case "peripherals.lighting":
                peripherals.SetKeyboardLighting(
                    String(values, "deviceId"), Int(values, "mode"),
                    Int(values, "primary"), Int(values, "secondary"),
                    Int(values, "speed"), Int(values, "brightness"));
                break;
            case "peripherals.profile": peripherals.SetKeyboardProfile(String(values, "deviceId"), Int(values, "value")); break;
            case "peripherals.energy":
                peripherals.SetKeyboardEnergy(String(values, "deviceId"), Int(values, "sleepMinutes"), Int(values, "lowBatteryWarning"));
                break;
            case "peripherals.oled":
                peripherals.SetKeyboardOled(String(values, "deviceId"), Bool(values.GetProperty("enabled")), Int(values, "brightness"), Int(values, "mode"));
                break;

            // The built-in pointer. A Home tile on the desktop with no companion action
            // at all, which made it the one control on that page the phone could not reach.
            case "input.touchpad": _services.GetRequiredService<IInputDeviceService>().ToggleTouchpad(); break;

            case "remote.enabled":
                Desktop.RemoteDesktopSettings.Enabled = Bool(value);
                if (!Bool(value)) _services.GetRequiredService<Desktop.RemoteDesktopServer>().DisconnectAll();
                break;
            case "remote.unattended": Desktop.RemoteDesktopSettings.Unattended = Bool(value); break;
            case "remote.input": Desktop.RemoteDesktopSettings.AllowInput = Bool(value); break;
            case "remote.audio": Desktop.RemoteDesktopSettings.AllowAudio = Bool(value); break;
            case "remote.clipboard": Desktop.RemoteDesktopSettings.AllowClipboard = Bool(value); break;
            case "remote.files": Desktop.RemoteDesktopSettings.AllowFiles = Bool(value); break;
            case "remote.lockOnDisconnect": Desktop.RemoteDesktopSettings.LockOnDisconnect = Bool(value); break;
            case "remote.consentSeconds": Desktop.RemoteDesktopSettings.ConsentSeconds = Int(value); break;
            case "remote.forgetTrusted": _services.GetRequiredService<Desktop.RemoteDesktopServer>().ForgetTrustedDevices(); break;
            case "remote.endSessions": _services.GetRequiredService<Desktop.RemoteDesktopServer>().DisconnectAll(); break;

            // Power, from the phone. Restarting was already here; the other three are the
            // ones somebody actually reaches for after closing a remote session.
            case "system.lock": Desktop.PrivacyGuard.LockWorkstation(); break;
            case "system.sleep":
                System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, force: false, disableWakeEvent: false);
                break;
            case "system.shutdown":
                System.Diagnostics.Process.Start(ProcessHelper.SystemPath("shutdown"), "/s /t 3");
                break;
            case "advanced.asusServices": await _services.GetRequiredService<AdvancedViewModel>().ToggleAsusServices(); break;
            case "advanced.fnLock": SetAdvanced(vm => vm.FnLockEnabled = Bool(value)); break;
            case "advanced.statusLeds": SetAdvanced(vm => vm.StatusLedEnabled = Bool(value)); break;
            case "advanced.numberPad": SetAdvanced(vm => vm.NumberPadEnabled = Bool(value)); break;
            case "advanced.overlay": SetOverlay(Bool(value)); break;
            case "advanced.overlayGaming": SetOverlayGaming(Bool(value)); break;
            case "advanced.pcie": SetAdvanced(vm => vm.AspmEnabled = Bool(value)); break;
            case "advanced.standbyNetwork": SetAdvanced(vm => vm.StandbyNetworkingEnabled = Bool(value)); break;
            case "advanced.closeGpuApps": SetAdvanced(vm => vm.KillGpuAppsEnabled = Bool(value)); break;
            case "advanced.usbcOptimized": SetAdvanced(vm => vm.OptimizedUsbCEnabled = Bool(value)); break;
            case "advanced.nvidiaPlatform": SetAdvanced(vm => vm.NvidiaPlatformEnabled = Bool(value)); break;
            case "advanced.disableOverdrive": SetAdvanced(vm => vm.DisableOverdrive = Bool(value)); break;
            case "advanced.forceOverdrive": SetAdvanced(vm => vm.ForceOverdrive = Bool(value)); break;
            case "advanced.autoClamshell": SetAdvanced(vm => vm.AutoClamshellEnabled = Bool(value)); break;
            case "advanced.alwaysOnTop": SetAdvanced(vm => vm.AlwaysOnTop = Bool(value)); break;
            case "advanced.autoTdp": SetAdvanced(vm => { if (vm.AutoTdpEnabled != Bool(value)) vm.ToggleAutoTdp(); }); break;
            case "advanced.fpsLimit": _services.GetRequiredService<AdvancedViewModel>().CycleFpsLimit(); break;
            case "advanced.bootSound": SetAdvanced(vm => vm.BootSoundEnabled = Bool(value)); break;
            case "advanced.hibernate": SetAdvanced(vm => vm.HibernateAfterMinutes = Int(value)); break;
            case "advanced.backlightBattery": SetAdvanced(vm => vm.KeyboardTimeoutSeconds = Int(value)); break;
            case "advanced.backlightAc": SetAdvanced(vm => vm.KeyboardAcTimeoutSeconds = Int(value)); break;
            case "advanced.powerOptions": _services.GetRequiredService<AdvancedViewModel>().OpenPowerPlanSettings(); break;
            case "advanced.log": _services.GetRequiredService<AdvancedViewModel>().OpenLog(); break;
            case "app.startup": SetSettings(vm => vm.RunOnStartup = Bool(value)); break;
            case "app.startMinimized": SetSettings(vm => vm.StartMinimized = Bool(value)); break;
            case "app.closeToTray": SetSettings(vm => vm.MinimizeToTray = Bool(value)); break;
            case "app.checkUpdates": SetSettings(vm => vm.CheckUpdatesOnStartup = Bool(value)); break;
            case "app.runSetup": _services.GetRequiredService<SettingsViewModel>().RunSetup(); break;
            case "app.theme": _services.GetRequiredService<SettingsViewModel>().SetTheme(Int(value)); break;
            case "app.toast": SetSettings(vm => vm.ToastEnabled = Bool(value)); break;
            case "app.toastStyle": SetSettings(vm => vm.ToastStyle = Int(value)); break;
            case "app.toastPosition": SetSettings(vm => vm.ToastPosition = Int(value)); break;
            case "app.toastDuration": SetSettings(vm => vm.ToastDurationSeconds = Int(value)); break;
            case "app.toastProgress": SetSettings(vm => vm.ToastProgress = Bool(value)); break;
            case "app.testToast": _services.GetRequiredService<SettingsViewModel>().TestToast(); break;

            // Answering a question the desktop is currently blocked on, and asking for a
            // restart outright - the second is what makes a pending reboot actionable
            // from the phone rather than only from the machine itself.
            case "system.answerPrompt":
                RemoteRestartPrompt.Answer(
                    values.ValueKind == JsonValueKind.Object ? String(values, "id") : null,
                    values.ValueKind == JsonValueKind.Object ? Bool(values.GetProperty("accepted")) : Bool(value));
                break;
            case "system.restart":
                System.Diagnostics.Process.Start(ProcessHelper.SystemPath("shutdown"), "/r /t 3");
                break;
            case "updates.check": await _services.GetRequiredService<IUpdateService>().CheckForUpdatesAsync(true); break;
            case "updates.asus": await _services.GetRequiredService<IUpdateService>().CheckAsusUpdatesAsync(); break;
            case "updates.install":
                var about = _services.GetRequiredService<AboutViewModel>();
                if (string.IsNullOrWhiteSpace(about.UpdateInfo.DownloadUrl)) await about.CheckForUpdate();
                await about.DownloadUpdate();
                break;
            case "app.openLanguage":
            case "app.openToastSettings": (System.Windows.Application.Current as App)?.ShowMainWindow(); break;
            default: throw new InvalidOperationException("Unsupported companion command: " + action);
        }

        if (action.StartsWith("slash.", StringComparison.Ordinal))
        {
            lock (_slashSettingsGate)
            {
                _cachedSlashSettings = null;
                _slashSettingsReadAt = 0;
            }
        }
    }

    /// <summary>
    /// Starts a phone-originated GPU switch on one worker and rejects overlap. The desktop
    /// UI blocks its own controls with the busy overlay; the phone needs the same guard or
    /// two quick requests can tear down and recreate NVIDIA services concurrently.
    /// </summary>
    private bool QueueRemoteGpuMode(IGpuService gpu, int mode)
    {
        if (_gpuSwitching || Interlocked.CompareExchange(ref _remoteGpuCommandPending, 1, 0) != 0)
            return false;

        // Close the small race with a switch that began between the first read and the
        // pending flag. Do not queue behind it: by then this selection may be stale.
        if (_gpuSwitching)
        {
            Volatile.Write(ref _remoteGpuCommandPending, 0);
            return false;
        }

        _ = Task.Run(() =>
        {
            using var scope = RemoteRestartPrompt.CommandScope();
            try
            {
                gpu.SetGpuMode(mode);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Companion GPU switch: " + ex.Message);
            }
            finally
            {
                // A real asynchronous transition owns the flag until its busy-false event.
                // Same-mode/no-op and rejected switches never raise busy, so release here.
                if (!_gpuSwitching) Volatile.Write(ref _remoteGpuCommandPending, 0);
            }
        });

        return true;
    }

    private void SetAdvanced(Action<AdvancedViewModel> change) => change(_services.GetRequiredService<AdvancedViewModel>());
    private void SetLighting(Action<LightingViewModel> change) => change(_services.GetRequiredService<LightingViewModel>());
    private void SetSettings(Action<SettingsViewModel> change) => change(_services.GetRequiredService<SettingsViewModel>());
    private void SetOverlay(bool enabled) { var vm = _services.GetRequiredService<AdvancedViewModel>(); if (vm.HardwareOverlayEnabled != enabled) vm.ToggleHardwareOverlay(); }
    private void SetOverlayGaming(bool enabled) { var vm = _services.GetRequiredService<AdvancedViewModel>(); if (vm.OverlayGameOnly != enabled) vm.ToggleOverlayGameOnly(); }
    private static void SetAutomationClamshell(bool enabled) { AppConfig.Set("clamshell", enabled ? 1 : 0); if (enabled) Program.clamshellControl?.ToggleLidAction(); else Arsenal.Helpers.ClamshellModeControl.DisableClamshellMode(); }

    private static void SaveCurve(ICoolingService cooling, int fanIndex, int mode, JsonElement values)
    {
        int[] temperatures = values.GetProperty("temperatures").EnumerateArray().Select(Int).Take(8).ToArray();
        int[] speeds = values.GetProperty("speeds").EnumerateArray().Select(Int).Take(8).ToArray();
        var curve = new FanCurveModel { FanIndex = fanIndex, FanName = fanIndex == 0 ? "CPU Fan" : "GPU Fan" };
        for (int i = 0; i < Math.Min(temperatures.Length, speeds.Length); i++) curve.Points.Add(new FanPoint(temperatures[i], speeds[i]));
        cooling.SaveFanCurve(fanIndex, mode, curve);
    }

    private static object Curve(FanCurveModel curve) => new { temperatures = curve.Points.Select(p => p.Temperature).ToArray(), speeds = curve.Points.Select(p => p.Percentage).ToArray() };
    private static int Positive(int value, int fallback) => value > 0 ? value : fallback;
    private static int PositiveOrZero(int value, int fallback) => value >= 0 ? value : fallback;
    private static int Int(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetInt32() : int.TryParse(value.GetString(), out int parsed) ? parsed : 0;
    private static bool Bool(JsonElement value) => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed) && parsed;
    private static int Int(JsonElement value, string property) => Int(value.GetProperty(property));
    /// <summary>
    /// A colour as <c>#RRGGBB</c>. Alpha is dropped: an accent is always painted opaque,
    /// and a phone parsing this should not have to decide what a translucent one means.
    /// </summary>
    private static string ToHex(System.Windows.Media.Color color)
        => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string String(JsonElement value, string property) => value.GetProperty(property).GetString() ?? string.Empty;

    private static void ShowRemoteNotification(string action, JsonElement request)
    {
        // These paths already emit Arsenal's native toast from the same backend
        // used by keyboard shortcuts. Keeping that original notification avoids a
        // second, slightly different toast for the same change.
        if (action is "performance.mode" or "lighting.brightness" or "app.testToast") return;

        JsonElement value = request.TryGetProperty("value", out JsonElement candidate) ? candidate : default;
        string message;
        ToastIcon icon = ToastIcon.Charger;

        switch (action)
        {
            case "performance.mode":
                message = Int(value) switch { 1 => "Turbo", 2 => "Silent", _ => "Balanced" };
                break;
            case "gpu.mode":
                message = Int(value) switch { 0 => "Eco", 2 => "Ultimate", 3 => "Optimized", _ => "Standard" };
                break;
            case "display.refresh":
                message = $"{Int(value)} Hz";
                icon = ToastIcon.BrightnessUp;
                break;
            case "display.overdrive":
                message = $"Panel overdrive {(Bool(value) ? "on" : "off")}";
                icon = Bool(value) ? ToastIcon.BrightnessUp : ToastIcon.BrightnessDown;
                break;
            case "display.miniLed":
                message = $"Mini-LED {(Bool(value) ? "on" : "off")}";
                icon = Bool(value) ? ToastIcon.BrightnessUp : ToastIcon.BrightnessDown;
                break;
            case "lighting.brightness":
                int brightness = Int(value);
                message = new[] { "Off", "Low", "Medium", "Maximum" }.ElementAtOrDefault(brightness) ?? $"Level {brightness}";
                icon = brightness == 0 ? ToastIcon.BacklightDown : ToastIcon.BacklightUp;
                break;
            case "battery.limit":
                message = $"Charge limit {Int(value)}%";
                icon = ToastIcon.Battery;
                break;
            case "advanced.fnLock":
                message = $"Fn lock {(Bool(value) ? "on" : "off")}";
                icon = ToastIcon.FnLock;
                break;
            default:
                string label = action.Split('.').LastOrDefault() ?? "setting";
                label = string.Concat(label.Select((character, index) => index > 0 && char.IsUpper(character) ? " " + char.ToLowerInvariant(character) : character.ToString()));
                message = value.ValueKind switch
                {
                    JsonValueKind.True => $"{label} on",
                    JsonValueKind.False => $"{label} off",
                    JsonValueKind.Number => $"{label} {value}",
                    JsonValueKind.String => $"{label} {value.GetString()}",
                    _ => label
                };
                message = char.ToUpperInvariant(message[0]) + message[1..];
                break;
        }

        ToastManager.Show(message, icon, "Changed from Mobile Companion");
    }

    private static bool ShowLiveRemoteNotification(string action, JsonElement request)
    {
        if (action is not ("display.panelBrightness" or "display.oledDimming")) return false;

        JsonElement valueElement = request.TryGetProperty("value", out JsonElement candidate) ? candidate : default;
        int value = Math.Clamp(Int(valueElement), 0, 100);
        string key = action == "display.panelBrightness" ? "display-brightness" : "oled-dimming";
        string message = action == "display.panelBrightness" ? "Display brightness" : "OLED dimming";
        ToastManager.ShowLive(key, message, ToastIcon.BrightnessUp, "Adjusting from Mobile Companion", value, 1000);
        return true;
    }

    private Task<T> OnUiAsync<T>(Func<T> work) => System.Windows.Application.Current.Dispatcher.InvokeAsync(work).Task;
    private Task<Task> OnUiAsync(Func<Task> work) => System.Windows.Application.Current.Dispatcher.InvokeAsync(work).Task;
    private Task OnUiAsync(Action work) => System.Windows.Application.Current.Dispatcher.InvokeAsync(work).Task;

    private static JsonDocument ParseBody(string body)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
        catch (JsonException) { throw new InvalidOperationException("Malformed JSON request."); }
    }

    private static async Task<HttpRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        // Consume complete TLS records rather than awaiting one byte at a time. The
        // old parser created hundreds of async continuations per request, which became
        // visible CPU overhead during realtime brightness dragging.
        using var received = new MemoryStream(4096);
        byte[] chunk = new byte[4096];
        int headerEnd = -1;
        while (received.Length < 32_768 && headerEnd < 0)
        {
            int remaining = 32_768 - (int)received.Length;
            int read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken);
            if (read == 0) break;
            int scanFrom = Math.Max(0, (int)received.Length - 3);
            received.Write(chunk, 0, read);
            byte[] buffer = received.GetBuffer();
            for (int i = scanFrom; i <= received.Length - 4; i++)
            {
                if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                {
                    headerEnd = i + 4;
                    break;
                }
            }
        }
        if (headerEnd < 0) throw new InvalidOperationException("Invalid HTTP request headers.");

        byte[] receivedBuffer = received.GetBuffer();
        string headerText = Encoding.ASCII.GetString(receivedBuffer, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        string[] first = lines[0].Split(' ', 3);
        if (first.Length < 2) throw new InvalidOperationException("Invalid HTTP request.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        int length = headers.TryGetValue("content-length", out string? lengthText) && int.TryParse(lengthText, out int parsedLength) ? Math.Clamp(parsedLength, 0, 128_000) : 0;
        byte[] body = new byte[length];
        int offset = Math.Min(length, (int)received.Length - headerEnd);
        if (offset > 0) Buffer.BlockCopy(receivedBuffer, headerEnd, body, 0, offset);
        while (offset < length)
        {
            int read = await stream.ReadAsync(body.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0) break;
            offset += read;
        }
        return new HttpRequest(first[0].ToUpperInvariant(), first[1].Split('?', 2)[0], headers, Encoding.UTF8.GetString(body, 0, offset));
    }

    private static async Task WriteResponseAsync(Stream stream, HttpResponse response, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(response.Body);
        string statusText = response.Status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", _ => "Internal Server Error" };
        string headers = $"HTTP/1.1 {response.Status} {statusText}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static HttpResponse Json(int status, object value) => new(status, JsonSerializer.Serialize(value, JsonOptions));
    private static HttpResponse Error(int status, string message) => Json(status, new { error = message });

    private void EnsureServerIdentity()
    {
        if (string.IsNullOrWhiteSpace(AppConfig.GetString("companion_server_id"))) AppConfig.Set("companion_server_id", Guid.NewGuid().ToString("N"));
        if (string.IsNullOrWhiteSpace(ReadToken())) WriteToken(NewToken());
    }
    private string GetToken() { EnsureServerIdentity(); return ReadToken(); }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The stored server token, or an empty string when there is not a readable one.
    /// </summary>
    /// <remarks>
    /// A token that cannot be unwrapped belongs to another Windows account - a copied or
    /// roamed profile - and is reported as absent, so a fresh one is minted rather than
    /// the bridge refusing every request with a credential nobody holds.
    /// </remarks>
    private static string ReadToken()
    {
        string stored = AppConfig.GetString("companion_token") ?? string.Empty;
        if (stored.Length == 0) return string.Empty;

        string? token = CompanionSecret.Unprotect(stored);
        if (token is null) return string.Empty;

        // Written by a build that stored it in the clear. Re-store it wrapped.
        if (!CompanionSecret.IsProtected(stored)) WriteToken(token);
        return token;
    }

    private static void WriteToken(string token) => AppConfig.Set("companion_token", CompanionSecret.Protect(token));

    private static bool SecureEquals(string presented, string expected)
    {
        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return presentedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }

    private static List<CompanionDeviceRecord> LoadDevices()
    {
        try
        {
            string stored = AppConfig.GetString("companion_devices") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stored)) return new List<CompanionDeviceRecord>();

            string? json = CompanionSecret.Unprotect(stored);
            if (json is null)
            {
                // Another account's records. Starting empty means every phone pairs
                // again, which is the same outcome as any other identity change here.
                Logger.WriteLine("Companion devices belong to another Windows account; starting with none paired");
                return new List<CompanionDeviceRecord>();
            }

            if (!CompanionSecret.IsProtected(stored))
                AppConfig.Set("companion_devices", CompanionSecret.Protect(json));

            return JsonSerializer.Deserialize<List<CompanionDeviceRecord>>(json, JsonOptions) ?? new List<CompanionDeviceRecord>();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Companion devices: " + ex.Message);
            return new List<CompanionDeviceRecord>();
        }
    }

    private void SaveDevices() =>
        AppConfig.Set("companion_devices", CompanionSecret.Protect(JsonSerializer.Serialize(_devices, JsonOptions)));

    private static X509Certificate2 LoadOrCreateCertificate()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arsenal", "Companion");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "identity.pfx");

        // Preserve the desktop TLS identity during the one-time brand migration so
        // previously paired devices do not see this PC as an unexpected replacement.
        if (!File.Exists(path))
        {
            string legacyBrand = string.Concat("G", "Helper");
            string legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                legacyBrand, "Companion", "identity.pfx");
            try
            {
                if (File.Exists(legacyPath)) File.Copy(legacyPath, path, overwrite: false);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Companion identity migration: " + ex.Message);
            }
        }

        if (File.Exists(path))
        {
            X509Certificate2? existing = LoadIdentity(path);
            if (existing is not null) return existing;

            // Unreadable: a partial write, a profile copied to another machine (DPAPI is
            // bound to the user), or a file that predates protection and is also corrupt.
            // A new identity means paired phones must pair again, which is the same
            // outcome as any other identity change and is what the pin is there for.
            Logger.WriteLine("Companion identity could not be read; generating a new one");
            try { File.Delete(path); } catch (Exception ex) { Logger.WriteLine("Companion identity delete: " + ex.Message); }
        }

        using RSA rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Arsenal Companion", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(8));

        File.WriteAllBytes(path, Protect(generated.Export(X509ContentType.Pfx)));
        return LoadIdentity(path) ?? throw new CryptographicException("The companion identity could not be created.");
    }

    /// <summary>
    /// The bytes this PFX is protected with. The file is DPAPI-wrapped for the current
    /// user, and this prefix is how a protected file is told from a plain one.
    /// </summary>
    /// <remarks>
    /// The identity used to be exported with a null password and written straight to
    /// disk, so the private key backing the bridge's TLS identity sat in the user's
    /// profile in the clear. That key is what a paired phone pins: a copy of it lets
    /// somebody impersonate this PC to a phone that already trusts it, and the pin
    /// notices nothing, because the certificate really is the right one.
    ///
    /// <para>DPAPI at CurrentUser scope ties the file to this Windows account, so it is
    /// useless if copied elsewhere - including into a backup or a roamed profile.</para>
    /// </remarks>
    private static ReadOnlySpan<byte> ProtectedMarker => "ARSNLPFX1"u8;

    private static byte[] Protect(byte[] pfx)
    {
        try
        {
            byte[] wrapped = System.Security.Cryptography.ProtectedData.Protect(
                pfx, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);

            byte[] output = new byte[ProtectedMarker.Length + wrapped.Length];
            ProtectedMarker.CopyTo(output);
            wrapped.CopyTo(output, ProtectedMarker.Length);
            return output;
        }
        catch (Exception ex)
        {
            // Losing the bridge entirely is worse than storing the identity the way
            // every previous release already did, so this falls back rather than throws.
            Logger.WriteLine("Companion identity could not be protected, storing unwrapped: " + ex.Message);
            return pfx;
        }
    }

    private static X509Certificate2? LoadIdentity(string path)
    {
        try
        {
            byte[] stored = File.ReadAllBytes(path);
            byte[] pfx = stored;

            if (stored.Length > ProtectedMarker.Length && stored.AsSpan(0, ProtectedMarker.Length).SequenceEqual(ProtectedMarker))
            {
                pfx = System.Security.Cryptography.ProtectedData.Unprotect(
                    stored.AsSpan(ProtectedMarker.Length).ToArray(), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
            }
            else
            {
                // Written by an earlier build, in the clear. Load it, then rewrite it
                // protected - the identity is preserved, so paired phones are unaffected.
                X509Certificate2 migrated = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                try
                {
                    File.WriteAllBytes(path, Protect(pfx));
                    Logger.WriteLine("Companion identity re-stored under user protection");
                }
                catch (Exception ex) { Logger.WriteLine("Companion identity re-store: " + ex.Message); }
                return migrated;
            }

            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Companion identity load: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Every address a phone could reach this laptop on, best first.
    /// </summary>
    /// <remarks>
    /// This used to return a single address: the first DHCP IPv4 across whichever
    /// adapters happened to be up. On a laptop holding Hyper-V, WSL, or a VPN adapter
    /// that regularly picked one nothing can route to, and that one guess was what the
    /// QR code carried and what the phone stored forever. Ranking puts the physical
    /// adapter that actually has a gateway in front, and everything else still travels
    /// as a fallback rather than being discarded.
    /// </remarks>
    private static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Select(unicast => (Adapter: adapter, unicast.Address)))
                .Where(entry => IsAdvertisableAddress(entry.Address))
                .OrderBy(entry => Rank(entry.Adapter, entry.Address))
                .Select(entry => entry.Address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }
        catch { return new[] { "127.0.0.1" }; }
    }

    private static bool IsAdvertisableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return !(bytes[0] == 169 && bytes[1] == 254);
        }
        // A link-local IPv6 address only means anything alongside the scope id of the
        // interface it belongs to, which is this machine's and not the phone's.
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.IsIPv6Teredo;
    }

    /// <summary>Lower sorts first.</summary>
    private static int Rank(NetworkInterface adapter, IPAddress address)
    {
        int score = 0;
        if (!adapter.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address is not null && gateway.Address.GetAddressBytes().Any(b => b != 0)))
            score += 4;   // nothing routes off this segment
        if (IsVirtualAdapter(adapter)) score += 8;
        if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) score += 6;
        if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211))
            score += 2;
        // IPv4 first because that is what a home network hands out; IPv6 stays in the
        // list because it is often the only thing that crosses between two subnets.
        if (address.AddressFamily == AddressFamily.InterNetworkV6) score += 1;
        return score;
    }

    private static bool IsVirtualAdapter(NetworkInterface adapter)
    {
        string text = adapter.Description + " " + adapter.Name;
        return VirtualAdapterMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] VirtualAdapterMarkers =
    {
        "hyper-v", "vethernet", "virtualbox", "vmware", "wsl", "docker",
        "tap-", "tunnel", "vpn", "bluetooth", "npcap", "loopback"
    };

    /// <summary>
    /// Names that follow the laptop when its address changes.
    /// </summary>
    /// <remarks>
    /// Windows answers mDNS for its own machine.local, and most home routers register
    /// DHCP client names in their resolver, so a name frequently resolves across the
    /// subnet boundary that a discovery broadcast cannot cross.
    /// </remarks>
    private static IReadOnlyList<string> LocalHostNames()
    {
        try
        {
            string host = Dns.GetHostName();
            if (string.IsNullOrWhiteSpace(host)) return Array.Empty<string>();

            var names = new List<string> { host };
            if (!host.Contains('.'))
            {
                names.Add(host + ".local");
                string suffix = IPGlobalProperties.GetIPGlobalProperties().DomainName;
                if (!string.IsNullOrWhiteSpace(suffix)) names.Add(host + "." + suffix);
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public void Dispose()
    {
        // Cancel() after the source has been disposed throws, so a second call here
        // used to fault the shutdown path rather than being the no-op Dispose promises.
        if (_disposed) return;
        _disposed = true;

        if (_subscribedToNetwork)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _subscribedToNetwork = false;
        }

        _stopping.Cancel();
        _listener?.Stop();
        _listener = null;
        _discoverySocket?.Dispose();
        _discoverySocket = null;
        _certificate?.Dispose();
        _stopping.Dispose();

        // _connections and _snapshotGate are deliberately not disposed. Handlers are
        // still in flight at this point and each one releases in a finally; releasing a
        // disposed SemaphoreSlim throws, so disposing here would turn shutdown into a
        // handful of unobserved exceptions. Neither semaphore ever hands out a wait
        // handle, which is the only thing that would need collecting.
    }

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, string Body, string RemoteAddress = "");
    private sealed record HttpResponse(int Status, string Body);
    private sealed class CompanionDeviceRecord
    {
        private string _token = string.Empty;
        private byte[]? _tokenBytes;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = "Android phone";
        public string Token
        {
            get => _token;
            set { _token = value ?? string.Empty; _tokenBytes = null; }
        }
        [JsonIgnore]
        public byte[] TokenBytes => _tokenBytes ??= Encoding.UTF8.GetBytes(_token);
        public string LastAddress { get; set; } = string.Empty;
        public DateTimeOffset PairedUtc { get; set; }
        public DateTimeOffset? LastSeenUtc { get; set; }
    }
}

public sealed record CompanionDeviceInfo(string Id, string Name, DateTimeOffset PairedUtc, DateTimeOffset? LastSeenUtc, string Address = "")
{
    public bool IsOnline => LastSeenUtc is not null && DateTimeOffset.UtcNow - LastSeenUtc < TimeSpan.FromSeconds(12);
    public string Status => IsOnline ? "Connected now" : LastSeenUtc is null ? "Not connected yet" : $"Last seen {LastSeenUtc.Value.ToLocalTime():g}";
    public string Paired => $"Paired {PairedUtc.ToLocalTime():g}";
    public string Details => string.IsNullOrWhiteSpace(Address) ? Paired : $"{Address}  ·  {Paired}";
}
