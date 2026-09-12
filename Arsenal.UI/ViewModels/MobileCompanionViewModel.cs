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
        ? AppStrings.Get("MobileCompanionUpdateNetworkAccess")
        : IsNetworkAccessAllowed ? AppStrings.Get("MobileCompanionNetworkAccessAllowed") : AppStrings.Get("MobileCompanionAllowNetworkAccess");

    [ObservableProperty]
    private ObservableCollection<CompanionDeviceInfo> _devices = new();

    [ObservableProperty]
    private ImageSource? _pairingQrCode;

    public MobileCompanionViewModel(RemoteCompanionService companion)
    {
        _companion = companion;
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
        ToastManager.Show(AppStrings.Get("MobileCompanionPhoneAccessRevoked"), ToastIcon.Charger, AppStrings.Get("MobileCompanionThatDeviceCanNoLonger"));
    }

    [RelayCommand]
    private void RevokeAllDevices()
    {
        _companion.RevokeAllDevices();
        RefreshAll();
        ToastManager.Show(AppStrings.Get("MobileCompanionCompanionAccessRevoked"), ToastIcon.Charger, AppStrings.Get("MobileCompanionPairYourPhonesAgainTo"));
    }

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
            started ? AppStrings.Get("MobileCompanionNetworkAccessAllowed") : AppStrings.Get("MobileCompanionNetworkPermissionUnchanged"),
            ToastIcon.Charger,
            started
                ? AppStrings.Get("MobileCompanionMobileCompanionCanNowAccept")
                : AppStrings.Get("MobileCompanionWindowsDidNotApproveThe"));
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
        PairingQrCode = null;
        Devices.Clear();
    }
}
