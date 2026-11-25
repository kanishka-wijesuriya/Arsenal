using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Peripherals;
using Arsenal.Peripherals.Headset;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Media;
using Wpf.Ui.Controls;
using MediaColor = System.Windows.Media.Color;

namespace Arsenal.UI.ViewModels
{
    /// <summary>
    /// One ASUS headset: G-Helper's headset window, section for section.
    /// </summary>
    /// <remarks>
    /// Like the mouse and keyboard pages, each setting is written as it changes. A
    /// headset keeps none of this on the PC: the device holds it, so what is shown is
    /// what was last read back.
    /// </remarks>
    public partial class HeadsetPeripheralViewModel : PeripheralViewModel
    {
        private AsusHeadset? _headset;

        /// <summary>The device's values for English, Chinese and a tone, in that order.</summary>
        private static readonly int[] VoicePromptValues = { 1, 2, 0 };

        private static readonly string[] BandLabels = { "32", "64", "125", "250", "500", "1K", "2K", "4K", "8K", "16K" };

        public HeadsetPeripheralViewModel(IPeripheralService service, PeripheralDeviceModel device)
            : base(service, device)
        {
            for (int i = 0; i < BandLabels.Length; i++)
                Bands.Add(new EqualizerBandViewModel(this, i, BandLabels[i] + " Hz"));

            Update(device);
            Load();
        }

        public override SymbolRegular Icon => SymbolRegular.Headphones24;

        public override void Update(PeripheralDeviceModel device)
        {
            AsusHeadset? headset = Service.FindHeadset(device.Id);
            if (headset is not null && !ReferenceEquals(headset, _headset))
            {
                if (_headset is not null) _headset.BatteryUpdated -= OnBatteryUpdated;
                _headset = headset;
                _headset.BatteryUpdated += OnBatteryUpdated;
            }
            base.Update(device);
        }

        private void OnBatteryUpdated(object? sender, EventArgs e) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_headset is null) return;
                BatteryText = _headset.BatteryText() + (_headset.Charging ? ", charging" : "");
            });

        protected override string Describe(PeripheralDeviceModel device)
        {
            string kind = device.HasBattery ? "Wireless headset" : "Headset";
            return HasBattery ? $"{kind}, battery {BatteryText}" : kind;
        }

        protected override void ReadDevice() => _headset?.SynchronizeDevice();

        protected override void LoadValues()
        {
            if (_headset is not { } h) return;

            LoadLighting(h);
            LoadAudio(h);
            LoadAnc(h);
            LoadPower(h);
            NotifyCapabilities();
        }

        private void NotifyCapabilities()
        {
            foreach (string name in new[]
            {
                nameof(HasLighting), nameof(HasBrightness), nameof(HasEqualizer), nameof(HasSidetone), nameof(MaxSidetone),
                nameof(HasNoiseReduction), nameof(HasNoiseReductionLevel), nameof(HasMicrophoneType), nameof(HasVoicePrompt),
                nameof(HasAudio), nameof(HasAnc), nameof(HasDirac), nameof(HasAncSection), nameof(HasPower),
                nameof(HasLowBatteryWarning), nameof(HasReset), nameof(MaxEqualizerGain),
            })
                OnPropertyChanged(name);
        }

        // ===== Lighting ===============================================================

        public bool HasLighting => _headset?.HasRGB() == true;
        public bool HasBrightness => _headset?.HasBrightness() == true;

        public ObservableCollection<PeripheralOptionModel> LightingModes { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsManualLighting))]
        private bool _auraSync;

        public bool IsManualLighting => !AuraSync;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowsColor))]
        private int _selectedMode;

        [ObservableProperty]
        private int _brightness = 100;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ColorBrush))]
        [NotifyPropertyChangedFor(nameof(ColorHex))]
        private MediaColor _color = Colors.Red;

        public SolidColorBrush ColorBrush => new(Color);
        public string ColorHex => Hex(Color);

        private HeadsetLightingMode Mode => (HeadsetLightingMode)SelectedMode;

        public bool ShowsColor => Mode is HeadsetLightingMode.Static or HeadsetLightingMode.Breathing or HeadsetLightingMode.Strobing or HeadsetLightingMode.Music;

        private void LoadLighting(AsusHeadset h)
        {
            AuraSync = PeripheralsProvider.IsHeadsetAuraSync;

            LightingModes.Clear();
            LightingModes.Add(new PeripheralOptionModel { Value = (int)HeadsetLightingMode.Off, Label = "Off" });
            foreach (HeadsetLightingMode mode in h.SupportedLightingModes())
                LightingModes.Add(new PeripheralOptionModel { Value = (int)mode, Label = ModeLabel(mode) });

            SelectedMode = (int)(LightingModes.Any(m => m.Value == (int)h.LightingMode) ? h.LightingMode : HeadsetLightingMode.Off);
            ReselectAfterRefill(nameof(SelectedMode));
            Brightness = Math.Clamp(h.Brightness, 0, 100);
            Color = ToMedia(h.LightingColor);
        }

        partial void OnAuraSyncChanged(bool value)
        {
            if (Loading || _headset is null) return;

            PeripheralsProvider.SetHeadsetAuraSync(value);
            if (value) Run(PeripheralsProvider.SyncHeadsetsWithAura, Load);
        }

        partial void OnSelectedModeChanged(int value)
        {
            if (Loading || _headset is not { } h || (HeadsetLightingMode)value == h.LightingMode) return;
            HeadsetLightingMode mode = (HeadsetLightingMode)value;
            int brightness = h.HasBrightness() ? Brightness : h.Brightness;
            System.Drawing.Color color = h.LightingColor;
            Run(() => h.SetLighting(mode, brightness, color));
        }

        partial void OnBrightnessChanged(int value)
        {
            if (Loading || _headset is not { } h) return;
            Settle("lighting", () =>
            {
                if (h.LightingMode == HeadsetLightingMode.Off) return null;
                int brightness = Brightness;
                return () => h.SetLighting(h.LightingMode, brightness, h.LightingColor);
            }, 150);
        }

        partial void OnColorChanged(MediaColor value)
        {
            if (Loading || _headset is not { } h) return;
            Settle("lighting", () =>
            {
                System.Drawing.Color color = ToDrawing(Color);
                int brightness = h.HasBrightness() ? Brightness : h.Brightness;
                return () => h.SetLighting(h.LightingMode, brightness, color);
            }, 150);
        }

        private static string ModeLabel(HeadsetLightingMode mode) => mode switch
        {
            HeadsetLightingMode.Static => "Static",
            HeadsetLightingMode.Breathing => "Breathing",
            HeadsetLightingMode.Strobing => "Strobe",
            HeadsetLightingMode.ColorCycle => "Colour cycle",
            HeadsetLightingMode.Rainbow => "Rainbow",
            HeadsetLightingMode.MqaIndicator => "MQA indicator",
            HeadsetLightingMode.Music => "Music",
            _ => mode.ToString(),
        };

        // ===== Audio ==================================================================

        public bool HasEqualizer => _headset?.HasEqualizer() == true;
        public bool HasSidetone => _headset?.HasSidetone() == true;
        public int MaxSidetone => _headset?.MaxSidetone() ?? 20;
        public bool HasNoiseReduction => _headset?.HasNoiseReduction() == true;
        public bool HasNoiseReductionLevel => _headset?.HasNoiseReductionLevel() == true;
        public bool HasMicrophoneType => _headset?.HasMicrophoneType() == true;
        public bool HasVoicePrompt => _headset?.HasVoicePrompt() == true;
        public bool HasAudio => HasEqualizer || HasSidetone || HasNoiseReduction || HasVoicePrompt || HasMicrophoneType;
        public int MaxEqualizerGain => _headset?.MaxEqualizerGain() ?? 100;

        public ObservableCollection<PeripheralOptionModel> EqualizerPresets { get; } = new();
        public ObservableCollection<EqualizerBandViewModel> Bands { get; } = new();

        [ObservableProperty]
        private bool _equalizerEnabled;

        /// <summary>The preset matching the current bands, or -1 when they match none.</summary>
        [ObservableProperty]
        private int _equalizerPreset = -1;

        [ObservableProperty]
        private bool _sidetoneEnabled;

        [ObservableProperty]
        private int _sidetone;

        [ObservableProperty]
        private bool _noiseReductionEnabled;

        [ObservableProperty]
        private int _noiseReduction;

        [ObservableProperty]
        private int _microphoneType;

        [ObservableProperty]
        private int _voicePrompt;

        public IReadOnlyList<PeripheralOptionModel> NoiseReductionOptions { get; } = Options("Low", "Medium", "High");
        public IReadOnlyList<PeripheralOptionModel> MicrophoneTypeOptions { get; } = Options("Boom", "Inline", "External");
        public IReadOnlyList<PeripheralOptionModel> VoicePromptOptions { get; } = Options("English", "Chinese", "Sound only");

        private (string Name, byte[] Gains)[] Presets => _headset?.EqualizerPresets ?? Array.Empty<(string, byte[])>();

        private void LoadAudio(AsusHeadset h)
        {
            EqualizerPresets.Clear();
            for (int i = 0; i < Presets.Length; i++)
                EqualizerPresets.Add(new PeripheralOptionModel { Value = i, Label = Presets[i].Name });
            for (int i = 1; i <= AsusHeadset.CustomPresets; i++)
                EqualizerPresets.Add(new PeripheralOptionModel { Value = Presets.Length + i - 1, Label = $"Custom {i}" });

            EqualizerEnabled = h.EqualizerEnabled;
            for (int i = 0; i < Bands.Count; i++) Bands[i].Gain = h.EqualizerGains[i];
            EqualizerPreset = FindPreset(h);
            ReselectAfterRefill(nameof(EqualizerPreset));

            SidetoneEnabled = h.SidetoneEnabled;
            Sidetone = h.Sidetone;
            NoiseReductionEnabled = h.NoiseReductionEnabled;
            NoiseReduction = h.NoiseReduction;
            MicrophoneType = h.MicrophoneType;
            VoicePrompt = Math.Max(0, Array.IndexOf(VoicePromptValues, h.VoicePrompt));
        }

        private byte[] PresetGains(AsusHeadset h, int index) =>
            index < Presets.Length ? Presets[index].Gains : h.GetCustomPreset(index - Presets.Length + 1);

        private int FindPreset(AsusHeadset h)
        {
            for (int i = 0; i < Presets.Length + AsusHeadset.CustomPresets; i++)
                if (PresetGains(h, i).SequenceEqual(h.EqualizerGains)) return i;
            return -1;
        }

        partial void OnEqualizerEnabledChanged(bool value)
        {
            if (!Loading && _headset is { } h) Run(() => h.SetEqualizer(value));
        }

        partial void OnEqualizerPresetChanged(int value)
        {
            if (Loading || _headset is not { } h || value < 0) return;

            byte[] gains = PresetGains(h, value);
            Show(() =>
            {
                EqualizerEnabled = true;
                for (int i = 0; i < Bands.Count; i++) Bands[i].Gain = gains[i];
            });
            Run(() => h.SetEqualizerGains(gains));
        }

        /// <summary>
        /// A band was moved. On a custom preset the change is kept in it; on a fixed one
        /// the list follows to whichever preset the bands now match, if any.
        /// </summary>
        internal void BandChanged(int band)
        {
            if (Loading || _headset is not { } h) return;

            Settle("band" + band, () =>
            {
                int gain = Bands[band].Gain;
                return () =>
                {
                    h.SetEqualizerBand(band, gain);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Show(() =>
                    {
                        EqualizerEnabled = true;
                        if (EqualizerPreset >= Presets.Length)
                            h.SaveCustomPreset(EqualizerPreset - Presets.Length + 1, h.EqualizerGains);
                        else
                            EqualizerPreset = FindPreset(h);
                    }));
                };
            }, 150);
        }

        partial void OnSidetoneEnabledChanged(bool value) => WriteSidetone();
        partial void OnSidetoneChanged(int value) => WriteSidetone();

        private void WriteSidetone()
        {
            if (Loading || _headset is not { } h) return;
            Settle("sidetone", () =>
            {
                bool enabled = SidetoneEnabled;
                int level = Math.Clamp(Sidetone, 0, h.MaxSidetone());
                return () => h.SetSidetone(enabled, level);
            });
        }

        partial void OnNoiseReductionEnabledChanged(bool value)
        {
            if (!Loading && _headset is { } h) Run(() => h.SetNoiseReduction(value, Math.Max(0, NoiseReduction)));
        }

        partial void OnNoiseReductionChanged(int value)
        {
            if (!Loading && _headset is { } h && value >= 0) Run(() => h.SetNoiseReduction(NoiseReductionEnabled, value));
        }

        partial void OnMicrophoneTypeChanged(int value)
        {
            if (!Loading && _headset is { } h && value >= 0) Run(() => h.SetMicrophoneType(value));
        }

        partial void OnVoicePromptChanged(int value)
        {
            if (!Loading && _headset is { } h && value >= 0 && value < VoicePromptValues.Length)
                Run(() => h.SetVoicePrompt(VoicePromptValues[value]));
        }

        // ===== Noise cancelling =======================================================

        public bool HasAnc => _headset?.HasAnc() == true;
        public bool HasDirac => _headset?.HasDirac() == true;
        public bool HasAncSection => HasAnc || HasDirac;

        public IReadOnlyList<PeripheralOptionModel> AncOptions { get; } = Options("Off", "On", "Ambient");
        public IReadOnlyList<PeripheralOptionModel> AncLevelOptions { get; } = Options("Auto", "Low", "Medium", "High");

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAncOn))]
        private int _ancMode;

        /// <summary>The level only applies while cancelling is on.</summary>
        public bool IsAncOn => AncMode == 1;

        [ObservableProperty]
        private bool _adaptiveAnc;

        [ObservableProperty]
        private int _ancLevel;

        [ObservableProperty]
        private bool _dirac;

        private void LoadAnc(AsusHeadset h)
        {
            AncMode = h.AncMode;
            AdaptiveAnc = h.AdaptiveAnc;
            AncLevel = h.AncLevel;
            Dirac = h.Dirac;
        }

        partial void OnAncModeChanged(int value)
        {
            if (!Loading && _headset is { } h && value >= 0) Run(() => h.SetAnc(value));
        }

        partial void OnAdaptiveAncChanged(bool value)
        {
            if (!Loading && _headset is { } h) Run(() => h.SetAncLevel(value, Math.Max(0, AncLevel)));
        }

        partial void OnAncLevelChanged(int value)
        {
            if (!Loading && _headset is { } h && value >= 0) Run(() => h.SetAncLevel(AdaptiveAnc, value));
        }

        partial void OnDiracChanged(bool value)
        {
            if (!Loading && _headset is { } h) Run(() => h.SetDirac(value));
        }

        // ===== Power ==================================================================

        public bool HasPower => _headset?.HasPowerSettings() == true;
        public bool HasLowBatteryWarning => _headset?.HasLowBatteryWarning() == true;
        public bool HasReset => _headset?.HasReset() == true;

        public ObservableCollection<PeripheralOptionModel> SleepOptions { get; } = new();

        [ObservableProperty]
        private int _sleepTimer;

        [ObservableProperty]
        private int _lowBatteryWarning;

        private void LoadPower(AsusHeadset h)
        {
            SleepOptions.Clear();
            foreach (int minutes in h.SleepTimers())
                SleepOptions.Add(new PeripheralOptionModel { Value = minutes, Label = minutes == 0 ? "Never" : minutes == 1 ? "1 minute" : $"{minutes} minutes" });
            SleepTimer = SleepOptions.Any(o => o.Value == h.SleepTimer) ? h.SleepTimer : SleepOptions.FirstOrDefault()?.Value ?? 0;
            ReselectAfterRefill(nameof(SleepTimer));
            LowBatteryWarning = Math.Clamp(h.LowBatteryWarning, 0, 50);
        }

        partial void OnSleepTimerChanged(int value)
        {
            if (!Loading && _headset is { } h) Run(() => h.SetEnergySettings(h.LowBatteryWarning, value));
        }

        partial void OnLowBatteryWarningChanged(int value)
        {
            if (Loading || _headset is not { } h) return;
            Settle("lowBattery", () =>
            {
                int warning = (int)Math.Round(Math.Clamp(LowBatteryWarning, 0, 50) / 10.0) * 10;
                return () => h.SetEnergySettings(warning, h.SleepTimer);
            });
        }

        /// <summary>Puts the headset back to its factory settings, then reads it again.</summary>
        [RelayCommand]
        private void Reset()
        {
            if (_headset is not { } h) return;
            IsReading = true;
            Run(() =>
            {
                h.ResetToDefaults();
                h.SynchronizeDevice();
            }, () => { Load(); IsReading = false; });
        }

        private static IReadOnlyList<PeripheralOptionModel> Options(params string[] labels) =>
            labels.Select((label, index) => new PeripheralOptionModel { Value = index, Label = label }).ToList();
    }

    /// <summary>One equalizer band.</summary>
    public partial class EqualizerBandViewModel : ObservableObject
    {
        private readonly HeadsetPeripheralViewModel _owner;

        public int Index { get; }
        public string Label { get; }

        [ObservableProperty]
        private int _gain;

        public EqualizerBandViewModel(HeadsetPeripheralViewModel owner, int index, string label)
        {
            _owner = owner;
            Index = index;
            Label = label;
        }

        partial void OnGainChanged(int value) => _owner.BandChanged(Index);
    }
}
