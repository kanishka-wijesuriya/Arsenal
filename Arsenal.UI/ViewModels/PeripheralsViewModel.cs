using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Peripherals;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace Arsenal.UI.ViewModels
{
    /// <summary>
    /// The Peripherals page: one entry per connected ASUS mouse, mouse pad, keyboard or headset,
    /// each of which opens onto that device's own settings.
    /// </summary>
    /// <remarks>
    /// Entries are kept by device and updated in place rather than rebuilt. Every
    /// battery report and every reconnection refreshes the service's list, and a
    /// rebuilt entry is a new subpage: the one the user was working in would be taken
    /// off screen and closed under them.
    /// </remarks>
    public partial class PeripheralsViewModel : ObservableObject
    {
        private readonly IPeripheralService _service;

        public ObservableCollection<PeripheralViewModel> Peripherals { get; } = new();

        [ObservableProperty]
        private bool _isScanning;

        public PeripheralsViewModel(IPeripheralService service)
        {
            _service = service;
            _service.DevicesChanged += () =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(Sync);
            Sync();
        }

        private void Sync()
        {
            IReadOnlyList<PeripheralDeviceModel> devices = _service.Devices;

            for (int i = Peripherals.Count - 1; i >= 0; i--)
            {
                if (!devices.Any(device => device.Id == Peripherals[i].Id))
                    Peripherals.RemoveAt(i);
            }

            for (int index = 0; index < devices.Count; index++)
            {
                PeripheralDeviceModel device = devices[index];
                PeripheralViewModel? entry = Peripherals.FirstOrDefault(p => p.Id == device.Id);

                if (entry is null)
                {
                    entry = device.IsKeyboard ? new KeyboardPeripheralViewModel(_service, device)
                        : device.IsHeadset ? new HeadsetPeripheralViewModel(_service, device)
                        : new MousePeripheralViewModel(_service, device);
                    Peripherals.Insert(Math.Min(index, Peripherals.Count), entry);
                }
                else
                {
                    entry.Update(device);
                }
            }
        }

        /// <summary>Looks for peripherals plugged in since the last look.</summary>
        [RelayCommand]
        private async Task Scan()
        {
            IsScanning = true;
            try
            {
                await Task.Run(() =>
                {
                    PeripheralsProvider.DetectAllAsusMice();
                    PeripheralsProvider.DetectAllAsusKeyboards();
                    PeripheralsProvider.DetectAllAsusHeadsets();
                    _service.RefreshDevices();
                });
            }
            finally
            {
                IsScanning = false;
            }
        }
    }
}
