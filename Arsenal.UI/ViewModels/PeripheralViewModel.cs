using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using Wpf.Ui.Controls;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace Arsenal.UI.ViewModels
{
    /// <summary>
    /// One connected peripheral: what the list shows, and the plumbing its settings
    /// share.
    /// </summary>
    /// <remarks>
    /// Every write goes through <see cref="Run"/>, one at a time per device and off the
    /// UI thread. A mouse answers each request before it takes the next, and two writes
    /// interleaved on the same HID handle get each other's replies.
    /// </remarks>
    public abstract partial class PeripheralViewModel : ObservableObject
    {
        protected readonly IPeripheralService Service;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, int> _settling = new();

        /// <summary>
        /// Set while values are being read back from the device, so that putting them
        /// on screen is not mistaken for the user changing them.
        /// </summary>
        protected bool Loading { get; private set; }

        public string Id { get; }

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _summary = string.Empty;

        [ObservableProperty]
        private bool _hasBattery;

        [ObservableProperty]
        private string _batteryText = string.Empty;

        /// <summary>True while the device is being read.</summary>
        [ObservableProperty]
        private bool _isReading;

        public abstract SymbolRegular Icon { get; }

        protected PeripheralViewModel(IPeripheralService service, PeripheralDeviceModel device)
        {
            Service = service;
            Id = device.Id;
        }

        /// <summary>Takes the list's latest reading of the device.</summary>
        public virtual void Update(PeripheralDeviceModel device)
        {
            Name = device.Name;
            HasBattery = device.HasBattery && device.BatteryPercentage >= 0;
            BatteryText = !HasBattery ? string.Empty
                : device.IsCharging ? $"{device.BatteryPercentage}%, charging"
                : $"{device.BatteryPercentage}%";
            Summary = Describe(device);
        }

        protected abstract string Describe(PeripheralDeviceModel device);

        /// <summary>
        /// Called when the device's own page is opened. Reads the device again, the way
        /// G-Helper does each time its window opens: settings changed on another PC, or
        /// by the device's own buttons, are otherwise not what is on screen.
        /// </summary>
        public void Opened()
        {
            IsReading = true;
            Run(ReadDevice, () =>
            {
                Load();
                IsReading = false;
            });
            StartListening();
        }

        /// <summary>Called when the device's page is left.</summary>
        public void Closed() => StopListening();

        /// <summary>
        /// Follows changes made with the device's own buttons while its page is open,
        /// as G-Helper does while its window for one is open.
        /// </summary>
        protected virtual void StartListening() { }

        protected virtual void StopListening() { }

        protected abstract void ReadDevice();

        /// <summary>Reads everything back from the device. G-Helper's Synchronize button.</summary>
        [RelayCommand]
        private void Synchronize() => Opened();

        /// <summary>Puts the device's current settings on screen.</summary>
        public void Load()
        {
            Loading = true;
            try { LoadValues(); }
            finally { Loading = false; }
        }

        protected abstract void LoadValues();

        /// <summary>
        /// Puts values on screen that the device already has, so they are not written
        /// back to it as though the user had changed them.
        /// </summary>
        protected void Show(Action apply)
        {
            bool wasLoading = Loading;
            Loading = true;
            try { apply(); }
            finally { Loading = wasLoading; }
        }

        /// <summary>
        /// Puts a list's selection back after its options were cleared and refilled.
        /// </summary>
        /// <remarks>
        /// Clearing the options drops the box's selection, and a value that is the same
        /// as before raises no change of its own, so nothing would select it again: the
        /// box sat empty behind a validation border, which is how Profile and Polling
        /// rate first rendered.
        /// </remarks>
        protected void ReselectAfterRefill(string property) => OnPropertyChanged(property);

        /// <summary>
        /// Queues a write to the device, and runs <paramref name="then"/> on the UI
        /// thread once it has finished.
        /// </summary>
        protected void Run(Action write, Action? then = null)
        {
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try { write(); }
                catch (Exception ex) { Logger.WriteLine($"{Name}: {ex}"); }
                finally { _gate.Release(); }

                if (then is not null)
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(then);
            });
        }

        /// <summary>
        /// Writes once a value has stopped changing.
        /// </summary>
        /// <remarks>
        /// A slider or colour picker produces a value on every mouse move, and each of
        /// these writes goes to the device's own memory. G-Helper writes on release; this
        /// writes once the value has been still for a moment, which is the same thing for
        /// a drag and also covers the keyboard and the colour field. The write is built
        /// when it happens, from whatever is on screen then.
        /// </remarks>
        protected void Settle(string key, Func<Action?> build, int delayMs = 250)
        {
            int generation = _settling.TryGetValue(key, out int current) ? current + 1 : 1;
            _settling[key] = generation;
            _ = SettleAsync(key, generation, build, delayMs);
        }

        private async Task SettleAsync(string key, int generation, Func<Action?> build, int delayMs)
        {
            await Task.Delay(delayMs);
            if (_settling[key] != generation) return;
            if (build() is { } write) Run(write);
        }

        protected static MediaColor ToMedia(DrawingColor color) => MediaColor.FromRgb(color.R, color.G, color.B);

        protected static DrawingColor ToDrawing(MediaColor color) => DrawingColor.FromArgb(color.R, color.G, color.B);

        protected static string Hex(MediaColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        public static IReadOnlyList<PeripheralOptionModel> PowerOffOptions { get; } = new[]
        {
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PowerOffSetting.Minutes1, Label = "1 minute" },
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PowerOffSetting.Minutes2, Label = "2 minutes" },
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PowerOffSetting.Minutes3, Label = "3 minutes" },
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PowerOffSetting.Minutes5, Label = "5 minutes" },
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PowerOffSetting.Minutes10, Label = "10 minutes" },
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PowerOffSetting.Never, Label = "Never" },
        };
    }
}
