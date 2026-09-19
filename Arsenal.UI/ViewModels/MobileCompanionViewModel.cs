using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Helpers;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using QRCoder;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Arsenal.UI.ViewModels;

public partial class MobileCompanionViewModel : ObservableObject, IDisposable
{
    private readonly RemoteCompanionService _companion;
    private readonly Services.Remote.Desktop.RemoteDesktopServer _remoteDesktop;
    private readonly DispatcherTimer _presenceTimer;
    private string _renderedPairingCode = string.Empty;
    private bool _isActive;

    public string CompanionAddress => _companion.Address;
    public string CompanionPairingCode => _companion.PairingCode;
    public string CompanionFingerprint => _companion.FingerprintDisplay;
    public string CompanionFingerprintBlock => _companion.FingerprintBlock;
    public bool HasPairedDevices => Devices.Count > 0;
    public bool IsNetworkAccessAllowed => CompanionFirewall.IsAllowed;
    public bool IsCompanionRunning => _companion.IsRunning;
    public string NetworkAccessButtonLabel => CompanionFirewall.NeedsRuleUpgrade
        ? "Update network access"
        : IsNetworkAccessAllowed ? "Network access allowed" : "Allow network access";

    [ObservableProperty]
    private ObservableCollection<CompanionDeviceInfo> _devices = new();

    [ObservableProperty]
    private ImageSource? _pairingQrCode;

    // ---- Remote control ----------------------------------------------------------
    //
    // Screen sharing is off until somebody turns it on here. Pairing proves the phone
    // belongs to whoever set this machine up, which is enough to read a temperature and
    // move a slider; it is not, on its own, enough to become a person sitting at the
    // keyboard. Each switch below says plainly what it gives away.

    public bool IsRemoteDesktopEnabled
    {
        get => Services.Remote.Desktop.RemoteDesktopSettings.Enabled;
        set
        {
            if (value == Services.Remote.Desktop.RemoteDesktopSettings.Enabled) return;
            Services.Remote.Desktop.RemoteDesktopSettings.Enabled = value;
            if (!value) _remoteDesktop.DisconnectAll();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RemoteStatus));
        }
    }

    public bool IsRemoteUnattended
    {
        get => Services.Remote.Desktop.RemoteDesktopSettings.Unattended;
        set { Services.Remote.Desktop.RemoteDesktopSettings.Unattended = value; OnPropertyChanged(); }
    }

    public bool IsRemoteInputAllowed
    {
        get => Services.Remote.Desktop.RemoteDesktopSettings.AllowInput;
        set { Services.Remote.Desktop.RemoteDesktopSettings.AllowInput = value; OnPropertyChanged(); }
    }

    public bool IsRemoteAudioAllowed
    {
        get => Services.Remote.Desktop.RemoteDesktopSettings.AllowAudio;
        set { Services.Remote.Desktop.RemoteDesktopSettings.AllowAudio = value; OnPropertyChanged(); }
    }

    public bool IsRemoteClipboardAllowed
    {
        get => Services.Remote.Desktop.RemoteDesktopSettings.AllowClipboard;
        set { Services.Remote.Desktop.RemoteDesktopSettings.AllowClipboard = value; OnPropertyChanged(); }
    }

    public bool IsRemoteFileTransferAllowed
    {
        get => Services.Remote.Desktop.RemoteDesktopSettings.AllowFiles;
        set { Services.Remote.Desktop.RemoteDesktopSettings.AllowFiles = value; OnPropertyChanged(); }
    }

    public bool IsRemoteLockOnDisconnect
    {
        get => Services.Remote.Desktop.RemoteDesktopSettings.LockOnDisconnect;
        set { Services.Remote.Desktop.RemoteDesktopSettings.LockOnDisconnect = value; OnPropertyChanged(); }
    }

    public string RemoteStatus => _remoteDesktop.StatusLine;
    public bool HasRemoteSessions => _remoteDesktop.ActiveSessions > 0;
    public string TrustedDevicesLabel => _remoteDesktop.TrustedDeviceCount switch
    {
        0 => "No phones are remembered",
        1 => "One phone will not be asked again",
        int count => count + " phones will not be asked again",
    };

    [RelayCommand]
    private void EndRemoteSessions()
    {
        _remoteDesktop.DisconnectAll();
        RefreshRemote();
    }

    [RelayCommand]
    private void ForgetRemoteDevices()
    {
        _remoteDesktop.ForgetTrustedDevices();
        RefreshRemote();
        ToastManager.Show("Remembered phones cleared", ToastIcon.Charger, "Every phone will be asked again before it takes the screen");
    }

    private void RefreshRemote()
    {
        OnPropertyChanged(nameof(RemoteStatus));
        OnPropertyChanged(nameof(HasRemoteSessions));
        OnPropertyChanged(nameof(TrustedDevicesLabel));
    }

    public MobileCompanionViewModel(RemoteCompanionService companion, Services.Remote.Desktop.RemoteDesktopServer remoteDesktop)
    {
        _companion = companion;
        _remoteDesktop = remoteDesktop;
        _remoteDesktop.SessionsChanged += OnRemoteSessionsChanged;
        _companion.DevicesChanged += OnDevicesChanged;
        _presenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _presenceTimer.Tick += OnPresenceTimerTick;
        RefreshAll();
    }

    public void SetActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        if (active)
        {
            RefreshAll();
            _presenceTimer.Start();
        }
        else
        {
            _presenceTimer.Stop();
            // The QR bitmap is cheap to regenerate from the current short pairing URI,
            // but otherwise stays retained for the whole tray session after one visit.
            PairingQrCode = null;
            _renderedPairingCode = string.Empty;

            // Leaving the page ends pairing. The bridge keeps serving paired phones;
            // it just stops accepting new ones, so a code is never live longer than
            // somebody is actually looking at it.
            _companion.ClosePairing();
        }
    }

    [RelayCommand]
    private void RegenerateCompanionCode()
    {
        _companion.RegeneratePairingCode();
        RefreshPairing();
    }

    [RelayCommand]
    private void RevokeDevice(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        _companion.RevokeDevice(deviceId);
        RefreshDevices();
        ToastManager.Show("Phone access revoked", ToastIcon.Charger, "That device can no longer control this laptop");
    }

    [RelayCommand]
    private void RevokeAllDevices()
    {
        _companion.RevokeAllDevices();
        RefreshAll();
        ToastManager.Show("Companion access revoked", ToastIcon.Charger, "Pair your phones again to reconnect");
    }

    private void OnRemoteSessionsChanged(object? sender, EventArgs e) => RefreshRemote();

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (_isActive) System.Windows.Application.Current.Dispatcher.BeginInvoke(RefreshAll);
    }

    private void OnPresenceTimerTick(object? sender, EventArgs e)
    {
        RefreshPairing();
        RefreshDevices();
    }

    private void RefreshAll()
    {
        OnPropertyChanged(nameof(CompanionAddress));
        OnPropertyChanged(nameof(IsNetworkAccessAllowed));
        OnPropertyChanged(nameof(IsCompanionRunning));
        OnPropertyChanged(nameof(NetworkAccessButtonLabel));
        RefreshPairing();
        RefreshDevices();
        RefreshRemote();
    }

    private void RefreshPairing()
    {
        if (CompanionFirewall.IsAllowed && !_companion.IsRunning) _companion.Start();
        OnPropertyChanged(nameof(IsNetworkAccessAllowed));
        OnPropertyChanged(nameof(IsCompanionRunning));
        OnPropertyChanged(nameof(NetworkAccessButtonLabel));
        string currentCode = _companion.PairingCode;
        if (currentCode == _renderedPairingCode) return;
        _renderedPairingCode = currentCode;
        OnPropertyChanged(nameof(CompanionPairingCode));
        OnPropertyChanged(nameof(CompanionFingerprint));
        OnPropertyChanged(nameof(CompanionFingerprintBlock));
        PairingQrCode = CreateQrCode(_companion.PairingUri);
    }

    [RelayCommand]
    private async Task AllowCompanionNetwork()
    {
        bool started = ProcessHelper.IsUserAdministrator()
            ? await Task.Run(() => CompanionFirewall.AllowPrivateNetwork())
            : await CompanionFirewall.RequestAccessAsync();

        if (CompanionFirewall.IsAllowed) _companion.Start();
        OnPropertyChanged(nameof(IsCompanionRunning));

        ToastManager.Show(
            started ? "Network access allowed" : "Network permission unchanged",
            ToastIcon.Charger,
            started
                ? "Mobile Companion can now accept connections from your private local network"
                : "Windows did not approve the firewall change");
        OnPropertyChanged(nameof(IsNetworkAccessAllowed));
        OnPropertyChanged(nameof(NetworkAccessButtonLabel));
    }

    private void RefreshDevices()
    {
        IReadOnlyList<CompanionDeviceInfo> latest = _companion.PairedDevices;
        if (Devices.SequenceEqual(latest)) return;
        Devices = new ObservableCollection<CompanionDeviceInfo>(latest);
        OnPropertyChanged(nameof(HasPairedDevices));
    }

    private static ImageSource CreateQrCode(string content)
    {
        using QRCodeData data = QRCodeGenerator.GenerateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(data);
        byte[] png = qrCode.GetGraphic(12);
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        _presenceTimer.Stop();
        _presenceTimer.Tick -= OnPresenceTimerTick;
        _companion.DevicesChanged -= OnDevicesChanged;
        _remoteDesktop.SessionsChanged -= OnRemoteSessionsChanged;
        PairingQrCode = null;
        Devices.Clear();
    }
}
