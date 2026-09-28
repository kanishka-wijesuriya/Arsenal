using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Peripherals;
using Arsenal.Peripherals.Mouse;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;
using MediaColor = System.Windows.Media.Color;

namespace Arsenal.UI.ViewModels
{
    /// <summary>
    /// One ASUS mouse or mouse pad: G-Helper's mouse window, section for section.
    /// </summary>
    /// <remarks>
    /// Every setting is shown only when the model has it and is written the moment it
    /// changes, as G-Helper does, rather than held for an Apply button: these all land in
    /// the device's own memory, and a value on screen that the mouse does not have yet
    /// is exactly the confusion the old page's Apply buttons caused.
    /// </remarks>
    public partial class MousePeripheralViewModel : PeripheralViewModel
    {
        private AsusMouse? _mouse;

        /// <summary>
        /// The colours G-Helper gives the stages of a mouse that has no colour of its
        /// own for them, so the stages can still be told apart at a glance.
        /// </summary>
        private static readonly MediaColor[] FixedStageColors =
        {
            Colors.Red, Colors.Purple, Colors.Blue, Colors.Green,
        };

        public MousePeripheralViewModel(IPeripheralService service, PeripheralDeviceModel device)
            : base(service, device)
        {
            Update(device);
            Load();
        }

        public override SymbolRegular Icon => IsPad ? SymbolRegular.RectangleLandscape24 : SymbolRegular.Cursor24;

        public override void Update(PeripheralDeviceModel device)
        {
            AsusMouse? mouse = Service.FindMouse(device.Id);
            if (mouse is not null && !ReferenceEquals(mouse, _mouse))
            {
                if (_mouse is not null) _mouse.BatteryUpdated -= OnBatteryUpdated;
                _mouse = mouse;
                _mouse.BatteryUpdated += OnBatteryUpdated;
            }
            base.Update(device);
        }

        private void OnBatteryUpdated(object? sender, EventArgs e) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_mouse is null) return;
                BatteryText = _mouse.Charging ? $"{_mouse.Battery}%, charging" : $"{_mouse.Battery}%";
            });

        /// <summary>A pad reports as a mouse but has no sensor.</summary>
        public bool IsPad => _mouse is not null && _mouse.DPIProfileCount() == 0;

        protected override string Describe(PeripheralDeviceModel device)
        {
            string kind = IsPad ? "Mouse pad" : _mouse?.Wireless == true ? "Wireless mouse" : "Mouse";
            if (HasBattery) return $"{kind}, battery {BatteryText}";
            if (IsPad || _mouse is null) return kind;
            return $"{kind}, {_mouse.DpiSettings.ElementAtOrDefault(Math.Max(0, _mouse.DpiProfile - 1))?.DPI ?? 0} DPI at {_mouse.PollingRateDisplayString(_mouse.PollingRate)}";
        }

        protected override void ReadDevice() => _mouse?.SynchronizeDevice();

        protected override void LoadValues()
        {
            if (_mouse is not { } m) return;

            LoadProfiles(m);
            LoadPerformance(m);
            LoadLighting(m);
            LoadPower(m);
            LoadBindings(m);
            NotifyCapabilities();
        }

        private void NotifyCapabilities()
        {
            foreach (string name in new[]
            {
                nameof(IsPad), nameof(Icon), nameof(HasProfiles), nameof(CanExport),
                nameof(HasPerformance), nameof(HasDpiColors), nameof(CanSetPollingRate), nameof(HasMotionSync),
                nameof(HasZoneMode), nameof(HasAngleSnapping), nameof(HasAngleTuning), nameof(HasAngleSettings),
                nameof(HasLiftOff), nameof(HasDebounce), nameof(HasAcceleration), nameof(HasDeceleration),
                nameof(HasLighting), nameof(HasLightingZones), nameof(HasAutoPowerOff), nameof(HasLowBatteryWarning),
                nameof(HasPower), nameof(HasBindings), nameof(HasSpeedShift),
                nameof(MinDpi), nameof(MaxDpi), nameof(DpiStep), nameof(AngleMin), nameof(AngleMax), nameof(AngleStep),
                nameof(DebounceMin), nameof(DebounceMax), nameof(MaxAcceleration), nameof(MaxDeceleration),
                nameof(MaxBrightness), nameof(BrightnessFormat), nameof(LowBatteryWarningMax), nameof(LowBatteryWarningStep),
            })
                OnPropertyChanged(name);
        }

        // ===== Profile ================================================================

        public bool HasProfiles => _mouse?.HasProfiles() == true && _mouse.ProfileCount() > 1;
        public bool CanExport => _mouse?.CanExport() == true;

        public ObservableCollection<PeripheralOptionModel> ProfileOptions { get; } = new();

        [ObservableProperty]
        private int _profile;

        private void LoadProfiles(AsusMouse m)
        {
            ProfileOptions.Clear();
            for (int i = 0; i < m.ProfileCount(); i++)
                ProfileOptions.Add(new PeripheralOptionModel { Value = i, Label = $"Profile {i + 1}" });
            Profile = m.Profile;
            ReselectAfterRefill(nameof(Profile));
        }

        partial void OnProfileChanged(int value)
        {
            if (Loading || _mouse is not { } m || m.Profile == value) return;

            // A profile carries its own DPI, lighting and bindings, so everything on the
            // page is read again once the mouse has switched.
            IsReading = true;
            Run(() =>
            {
                m.SetProfile(value);
                m.SynchronizeDevice();
            }, () => { Load(); IsReading = false; });
        }

        [RelayCommand]
        private void Export()
        {
            if (_mouse is not { } m) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = ProfileFileFilter,
                FileName = m.GetDisplayName(),
            };
            if (dialog.ShowDialog() != true) return;

            string path = dialog.FileName;
            Run(() => File.WriteAllBytes(path, m.Export()));
        }

        [RelayCommand]
        private void Import()
        {
            if (_mouse is not { } m) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = ProfileFileFilter };
            if (dialog.ShowDialog() != true) return;

            byte[] data = File.ReadAllBytes(dialog.FileName);
            bool imported = false;
            IsReading = true;
            Run(() => imported = m.Import(data), () =>
            {
                IsReading = false;
                if (!imported)
                {
                    System.Windows.MessageBox.Show(
                        "This file is not a profile for this mouse, or the mouse did not accept it.",
                        m.GetDisplayName());
                    return;
                }
                Load();
            });
        }

        /// <summary>
        /// G-Helper's own format and extension, so a profile saved from either
        /// application opens in the other.
        /// </summary>
        private const string ProfileFileFilter = "Mouse profile (*.gmp1)|*.gmp1";

        // ===== Performance ============================================================

        public bool HasPerformance => _mouse is not null && _mouse.DPIProfileCount() > 0;
        public bool HasDpiColors => _mouse?.HasDPIColors() == true;
        public int MinDpi => _mouse?.MinDPI() ?? 100;
        public int MaxDpi => _mouse?.MaxDPI() ?? 2000;
        public int DpiStep => Math.Max(1, _mouse?.DPIIncrements() ?? 50);

        public ObservableCollection<DpiStageViewModel> DpiStages { get; } = new();

        [ObservableProperty]
        private bool _canAddDpiStage;

        [ObservableProperty]
        private int _activeDpi = 800;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ActiveDpiHex))]
        [NotifyPropertyChangedFor(nameof(ActiveDpiBrush))]
        private MediaColor _activeDpiColor = Colors.Red;

        public string ActiveDpiHex => Hex(ActiveDpiColor);
        public SolidColorBrush ActiveDpiBrush => new(ActiveDpiColor);

        public bool CanSetPollingRate => _mouse?.CanSetPollingRate() == true;
        public ObservableCollection<PeripheralOptionModel> PollingRateOptions { get; } = new();

        [ObservableProperty]
        private int _selectedPollingRate;

        public bool HasMotionSync => _mouse?.HasMotionSync() == true;

        [ObservableProperty]
        private bool _motionSync;

        /// <summary>Motion Sync does nothing at 8000 Hz, and G-Helper greys it out there.</summary>
        public bool CanUseMotionSync => SelectedPollingRate != (int)Arsenal.Peripherals.Mouse.PollingRate.PR8000Hz;

        public bool HasZoneMode => _mouse?.HasZoneMode() == true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsStageControlEnabled))]
        private bool _zoneMode;

        /// <summary>Zone Mode overrides the stages and the rate, which G-Helper disables while it is on.</summary>
        public bool IsStageControlEnabled => !ZoneMode;

        [ObservableProperty]
        private int _zoneModeDpi = 1600;

        public IReadOnlyList<PeripheralOptionModel> ZoneModePollingRateOptions { get; } = new[]
        {
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PollingRate.PR1000Hz, Label = "1000 Hz" },
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PollingRate.PR2000Hz, Label = "2000 Hz" },
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PollingRate.PR4000Hz, Label = "4000 Hz" },
            new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.PollingRate.PR8000Hz, Label = "8000 Hz" },
        };

        [ObservableProperty]
        private int _zoneModePollingRate = (int)Arsenal.Peripherals.Mouse.PollingRate.PR4000Hz;

        public bool HasAngleSnapping => _mouse?.HasAngleSnapping() == true;
        public bool HasAngleTuning => _mouse?.HasAngleTuning() == true;
        public bool HasAngleSettings => HasAngleSnapping || HasAngleTuning;
        public int AngleMin => _mouse?.AngleTuningMin() ?? -20;
        public int AngleMax => _mouse?.AngleTuningMax() ?? 20;
        public int AngleStep => Math.Max(1, _mouse?.AngleTuningStep() ?? 1);

        [ObservableProperty]
        private bool _angleSnapping;

        [ObservableProperty]
        private int _angleAdjustment;

        public bool HasLiftOff => _mouse?.HasLiftOffSetting() == true;

        public IReadOnlyList<PeripheralOptionModel> LiftOffOptions { get; } = new[]
        {
            new PeripheralOptionModel { Value = (int)LiftOffDistance.Low, Label = "Low" },
            new PeripheralOptionModel { Value = (int)LiftOffDistance.High, Label = "High" },
        };

        [ObservableProperty]
        private int _liftOff;

        public bool HasDebounce => _mouse?.HasDebounceSetting() == true;
        public int DebounceMin => (int)(_mouse?.MinDebounce() ?? DebounceTime.MS8);
        public int DebounceMax => (int)(_mouse?.MaxDebounce() ?? DebounceTime.MS32);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DebounceText))]
        private int _debounce;

        /// <summary>The setting is a step number; what the mouse waits is in milliseconds.</summary>
        public string DebounceText => _mouse is null ? string.Empty : $"{_mouse.DebounceTimeInMS((DebounceTime)Debounce)} ms";

        public bool HasAcceleration => _mouse?.HasAcceleration() == true;
        public bool HasDeceleration => _mouse?.HasDeceleration() == true;
        public int MaxAcceleration => _mouse?.MaxAcceleration() ?? 9;
        public int MaxDeceleration => _mouse?.MaxDeceleration() ?? 9;

        [ObservableProperty]
        private int _acceleration;

        [ObservableProperty]
        private int _deceleration;

        private void LoadPerformance(AsusMouse m)
        {
            if (!HasPerformance) return;

            LoadStages(m);

            PollingRateOptions.Clear();
            if (m.CanSetPollingRate())
            {
                foreach (Arsenal.Peripherals.Mouse.PollingRate rate in m.SupportedPollingrates())
                    PollingRateOptions.Add(new PeripheralOptionModel { Value = (int)rate, Label = m.PollingRateDisplayString(rate) });
            }
            SelectedPollingRate = (int)m.PollingRate;
            ReselectAfterRefill(nameof(SelectedPollingRate));
            OnPropertyChanged(nameof(CanUseMotionSync));
            MotionSync = CanUseMotionSync && m.MotionSync;

            ZoneMode = m.ZoneMode;
            ZoneModeDpi = Math.Clamp(m.ZoneModeDPI, MinDpi, MaxDpi);
            ZoneModePollingRate = (int)m.ZoneModePollingRate;

            AngleSnapping = m.AngleSnapping;
            AngleAdjustment = Math.Clamp((int)m.AngleAdjustmentDegrees, AngleMin, AngleMax);
            LiftOff = (int)m.LiftOffDistance;
            Debounce = (int)m.Debounce;
            Acceleration = m.Acceleration;
            Deceleration = m.Deceleration;
        }

        private void LoadStages(AsusMouse m)
        {
            DpiStages.Clear();
            int count = Math.Min(m.CurrentDPIProfileCount > 0 ? m.CurrentDPIProfileCount : m.DPIProfileCount(), m.DpiSettings.Length);
            bool canChange = m.CanChangeDPICount();

            for (int i = 0; i < count; i++)
            {
                AsusMouseDPI? stage = m.DpiSettings[i];
                if (stage is null) continue;

                MediaColor color = m.HasDPIColors() ? ToMedia(stage.Color) : FixedStageColors[i % FixedStageColors.Length];
                DpiStages.Add(new DpiStageViewModel(i, (int)stage.DPI, color, ActiveStageNumber(m) - 1 == i, canChange && count > 2));
            }

            CanAddDpiStage = canChange && count < m.DPIProfileCount();

            if (m.DpiSettings.Length > 0 && m.DpiSettings[ActiveStageNumber(m) - 1] is { } active)
            {
                ActiveDpi = Math.Clamp((int)active.DPI, MinDpi, MaxDpi);
                ActiveDpiColor = ToMedia(active.Color);
            }
        }

        [RelayCommand]
        private void SelectDpiStage(DpiStageViewModel? stage)
        {
            if (stage is null || _mouse is not { } m || ZoneMode) return;
            Run(() => m.SetDPIProfile(stage.Index + 1), () => LoadPartial(() => LoadStages(m)));
        }

        [RelayCommand]
        private void AddDpiStage()
        {
            if (_mouse is not { } m || !CanAddDpiStage) return;
            Run(m.AddDPIProfile, () => LoadPartial(() => LoadStages(m)));
        }

        [RelayCommand]
        private void DeleteDpiStage(DpiStageViewModel? stage)
        {
            if (stage is null || _mouse is not { } m || !stage.CanDelete) return;
            Run(() => m.DeleteDPIProfile(stage.Index), () => LoadPartial(() => LoadStages(m)));
        }

        /// <summary>
        /// Set while one section is read back after a write, the way a full load is:
        /// the stage list, the zone and the bindings are refreshed on their own.
        /// </summary>
        private bool _partial;

        private void LoadPartial(Action reload)
        {
            _partial = true;
            try { reload(); }
            finally { _partial = false; }
        }

        /// <summary>Whether a value arriving now came from the user.</summary>
        private bool FromUser => !Loading && !_partial;

        partial void OnActiveDpiChanged(int value)
        {
            if (FromUser && ActiveStage() is { } stage) stage.Dpi = value;
            if (!FromUser || _mouse is not { } m) return;

            Settle("dpi", () =>
            {
                int profile = ActiveStageNumber(m);
                if (profile < 1 || profile > m.DpiSettings.Length || m.DpiSettings[profile - 1] is not { } dpi) return null;
                uint target = (uint)Math.Clamp(ActiveDpi, MinDpi, MaxDpi);
                return () =>
                {
                    dpi.DPI = target;
                    m.SetDPIForProfile(dpi, profile);
                };
            });
        }

        partial void OnActiveDpiColorChanged(MediaColor value)
        {
            if (FromUser && ActiveStage() is { } stage) stage.Color = value;
            if (!FromUser || _mouse is not { } m || !m.HasDPIColors()) return;

            Settle("dpiColor", () =>
            {
                int profile = ActiveStageNumber(m);
                if (profile < 1 || profile > m.DpiSettings.Length || m.DpiSettings[profile - 1] is not { } dpi) return null;
                System.Drawing.Color color = ToDrawing(ActiveDpiColor);
                return () =>
                {
                    dpi.Color = color;
                    m.SetDPIForProfile(dpi, profile);
                };
            });
        }

        private DpiStageViewModel? ActiveStage() => DpiStages.FirstOrDefault(stage => stage.IsActive);

        /// <summary>
        /// The stage in use, numbered from one as the mouse numbers them. A reply that
        /// says 0 is read as the first stage rather than as none, which left the page
        /// with no stage selected and every DPI write addressed to nothing.
        /// </summary>
        private static int ActiveStageNumber(AsusMouse m) =>
            Math.Clamp(m.DpiProfile, 1, Math.Max(1, m.DpiSettings.Length));

        partial void OnSelectedPollingRateChanged(int value)
        {
            OnPropertyChanged(nameof(CanUseMotionSync));
            if (!FromUser || _mouse is not { } m) return;

            var rate = (Arsenal.Peripherals.Mouse.PollingRate)value;
            Run(() => m.SetPollingRate(rate), () =>
            {
                LoadPartial(() => MotionSync = CanUseMotionSync && m.MotionSync);
            });
        }

        partial void OnMotionSyncChanged(bool value)
        {
            if (FromUser && _mouse is { } m) Run(() => m.SetMotionSync(value));
        }

        partial void OnZoneModeChanged(bool value)
        {
            if (FromUser && _mouse is { } m) Run(() => m.SetZoneMode(value));
        }

        partial void OnZoneModeDpiChanged(int value)
        {
            if (!FromUser || _mouse is not { } m) return;
            Settle("zoneDpi", () =>
            {
                int dpi = ZoneModeDpi;
                return () =>
                {
                    m.ZoneModeDPI = dpi;
                    if (m.ZoneMode) m.UpdateZoneModeDPI(dpi);
                };
            });
        }

        partial void OnZoneModePollingRateChanged(int value)
        {
            if (!FromUser || _mouse is not { } m) return;
            var rate = (Arsenal.Peripherals.Mouse.PollingRate)value;
            Run(() =>
            {
                m.ZoneModePollingRate = rate;
                if (m.ZoneMode) m.UpdateZoneModePollingRate(rate);
            });
        }

        partial void OnAngleSnappingChanged(bool value)
        {
            if (!FromUser || _mouse is not { } m) return;
            short angle = (short)AngleAdjustment;
            Run(() =>
            {
                m.SetAngleSnapping(value);
                m.SetAngleAdjustment(angle);
            });
        }

        partial void OnAngleAdjustmentChanged(int value)
        {
            if (!FromUser || _mouse is not { } m) return;
            Settle("angle", () =>
            {
                short angle = (short)AngleAdjustment;
                return () => m.SetAngleAdjustment(angle);
            });
        }

        partial void OnLiftOffChanged(int value)
        {
            if (FromUser && _mouse is { } m) Run(() => m.SetLiftOffDistance((LiftOffDistance)value));
        }

        partial void OnDebounceChanged(int value)
        {
            if (!FromUser || _mouse is not { } m) return;
            Settle("debounce", () =>
            {
                var time = (DebounceTime)Debounce;
                return () => m.SetDebounce(time);
            });
        }

        partial void OnAccelerationChanged(int value)
        {
            if (!FromUser || _mouse is not { } m) return;
            Settle("acceleration", () =>
            {
                int level = Acceleration;
                return () => m.SetAcceleration(level);
            });
        }

        partial void OnDecelerationChanged(int value)
        {
            if (!FromUser || _mouse is not { } m) return;
            Settle("deceleration", () =>
            {
                int level = Deceleration;
                return () => m.SetDeceleration(level);
            });
        }

        // ===== Lighting ===============================================================

        public bool HasLighting => _mouse?.HasRGB() == true;
        public int MaxBrightness => _mouse?.MaxBrightness() ?? 100;

        /// <summary>Most mice take a percentage; a Balteus has four steps, and "4%" would misread.</summary>
        public string BrightnessFormat => MaxBrightness == 100 ? "{0}%" : $"{{0}} of {MaxBrightness}";

        public ObservableCollection<PeripheralOptionModel> LightingZones { get; } = new();
        public bool HasLightingZones => LightingZones.Count > 1;

        public ObservableCollection<MouseLightingModeOption> LightingModes { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsManualLighting))]
        private bool _auraSync;

        public bool IsManualLighting => !AuraSync;

        [ObservableProperty]
        private int _selectedZone = (int)Arsenal.Peripherals.Mouse.LightingZone.All;

        [ObservableProperty]
        private int _selectedMode;

        [ObservableProperty]
        private int _brightness;

        [ObservableProperty]
        private bool _randomColor;

        [ObservableProperty]
        private int _speed = 1;

        [ObservableProperty]
        private int _direction;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LightingHex))]
        [NotifyPropertyChangedFor(nameof(LightingBrush))]
        private MediaColor _lightingColor = Colors.Red;

        public string LightingHex => Hex(LightingColor);
        public SolidColorBrush LightingBrush => new(LightingColor);

        private MouseLightingModeOption? CurrentMode => LightingModes.FirstOrDefault(mode => mode.Value == SelectedMode);
        public bool ShowsRandomColor => CurrentMode?.HasRandomColor == true;
        public bool ShowsColor => CurrentMode?.HasColor == true && !(ShowsRandomColor && RandomColor);
        public bool ShowsSpeed => CurrentMode?.HasSpeed == true;
        public bool ShowsDirection => CurrentMode?.HasDirection == true;

        private void LoadLighting(AsusMouse m)
        {
            AuraSync = PeripheralsProvider.IsAuraSync;

            LightingZones.Clear();
            if (!m.HasRGB()) return;

            LightingZone[] zones = m.SupportedLightingZones();
            LightingZones.Add(new PeripheralOptionModel { Value = (int)Arsenal.Peripherals.Mouse.LightingZone.All, Label = "All zones" });
            if (zones.Length > 1)
            {
                foreach (LightingZone zone in zones)
                    LightingZones.Add(new PeripheralOptionModel { Value = (int)zone, Label = ZoneLabel(zone) });
            }
            OnPropertyChanged(nameof(HasLightingZones));

            // A mouse lit zone by zone opens on its first zone rather than on All, which
            // would show one zone's settings as though they were everyone's.
            var shown = (LightingZone)SelectedZone;
            if (zones.Length > 1 && m.IsLightingZoned() && shown == Arsenal.Peripherals.Mouse.LightingZone.All)
                shown = zones[0];
            if (!LightingZones.Any(z => z.Value == (int)shown)) shown = Arsenal.Peripherals.Mouse.LightingZone.All;
            SelectedZone = (int)shown;
            ReselectAfterRefill(nameof(SelectedZone));
            LoadZone(m, shown);
        }

        private void LoadZone(AsusMouse m, LightingZone zone)
        {
            LightingModes.Clear();
            foreach (LightingMode mode in Enum.GetValues<LightingMode>())
            {
                if (!m.IsLightingModeSupported(mode) || !m.IsLightingModeSupportedForZone(mode, zone)) continue;
                LightingModes.Add(new MouseLightingModeOption
                {
                    Value = (int)mode,
                    Label = ModeLabel(mode),
                    HasColor = m.SupportsColorSetting(mode),
                    HasRandomColor = m.SupportsRandomColor(mode),
                    HasSpeed = m.SupportsAnimationSpeed(mode),
                    HasDirection = m.SupportsAnimationDirection(mode),
                });
            }

            LightingSetting setting = m.LightingSettingForZone(zone) ?? new LightingSetting();
            SelectedMode = (int)setting.LightingMode;
            OnPropertyChanged(nameof(SelectedMode));
            Brightness = Math.Clamp(setting.Brightness, 0, m.MaxBrightness());
            RandomColor = setting.RandomColor;
            Speed = setting.AnimationSpeed switch { AnimationSpeed.Slow => 0, AnimationSpeed.Fast => 2, _ => 1 };
            Direction = setting.AnimationDirection == AnimationDirection.CounterClockwise ? 1 : 0;
            LightingColor = ToMedia(setting.RGBColor);
            NotifyModeOptions();
        }

        private void NotifyModeOptions()
        {
            OnPropertyChanged(nameof(ShowsRandomColor));
            OnPropertyChanged(nameof(ShowsColor));
            OnPropertyChanged(nameof(ShowsSpeed));
            OnPropertyChanged(nameof(ShowsDirection));
        }

        partial void OnAuraSyncChanged(bool value)
        {
            if (!FromUser || _mouse is not { } m) return;
            PeripheralsProvider.SetAuraSync(value);
            if (value) Run(() => m.SyncFromKeyboardAura(), () => LoadPartial(() => LoadLighting(m)));
        }

        partial void OnSelectedZoneChanged(int value)
        {
            if (!FromUser || _mouse is not { } m) return;
            LoadPartial(() => LoadZone(m, (LightingZone)value));
        }

        partial void OnSelectedModeChanged(int value) { NotifyModeOptions(); WriteLighting(); }
        partial void OnBrightnessChanged(int value) => WriteLighting();
        partial void OnRandomColorChanged(bool value) { OnPropertyChanged(nameof(ShowsColor)); WriteLighting(); }
        partial void OnSpeedChanged(int value) => WriteLighting();
        partial void OnDirectionChanged(int value) => WriteLighting();
        partial void OnLightingColorChanged(MediaColor value) => WriteLighting();

        private void WriteLighting()
        {
            if (!FromUser || AuraSync || _mouse is not { } m) return;

            Settle("lighting", () =>
            {
                var zone = (LightingZone)SelectedZone;
                var mode = (LightingMode)SelectedMode;
                if (!m.IsLightingModeSupported(mode) || !m.IsLightingModeSupportedForZone(mode, zone)) return null;

                // A fresh setting rather than the cached one: writing All shares a single
                // instance across every zone, so editing the cache in place would change
                // the other zones' readouts before the write had happened.
                LightingSetting current = m.LightingSettingForZone(zone) ?? new LightingSetting();
                var setting = new LightingSetting
                {
                    LightingMode = mode,
                    Brightness = Math.Clamp(Brightness, 0, m.MaxBrightness()),
                    RGBColor = m.SupportsColorSetting(mode) ? ToDrawing(LightingColor) : current.RGBColor,
                    RandomColor = m.SupportsRandomColor(mode) && RandomColor,
                    AnimationSpeed = Speed switch { 0 => AnimationSpeed.Slow, 2 => AnimationSpeed.Fast, _ => AnimationSpeed.Medium },
                    AnimationDirection = Direction == 1 ? AnimationDirection.CounterClockwise : AnimationDirection.Clockwise,
                };
                return () => m.SetLightingSetting(setting, zone);
            }, 150);
        }

        private static string ZoneLabel(LightingZone zone) => zone switch
        {
            Arsenal.Peripherals.Mouse.LightingZone.Scrollwheel => "Scroll wheel",
            Arsenal.Peripherals.Mouse.LightingZone.Underglow => "Underglow",
            Arsenal.Peripherals.Mouse.LightingZone.Logo => "Logo",
            Arsenal.Peripherals.Mouse.LightingZone.Dock => "Dock",
            _ => "All zones",
        };

        private static string ModeLabel(LightingMode mode) => mode switch
        {
            Arsenal.Peripherals.Mouse.LightingMode.Static => "Static",
            Arsenal.Peripherals.Mouse.LightingMode.Breathing => "Breathing",
            Arsenal.Peripherals.Mouse.LightingMode.ColorCycle => "Colour cycle",
            Arsenal.Peripherals.Mouse.LightingMode.Rainbow => "Rainbow",
            Arsenal.Peripherals.Mouse.LightingMode.React => "React",
            Arsenal.Peripherals.Mouse.LightingMode.Comet => "Comet",
            Arsenal.Peripherals.Mouse.LightingMode.BatteryState => "Battery level",
            Arsenal.Peripherals.Mouse.LightingMode.Off => "Off",
            _ => mode.ToString(),
        };

        // ===== Power ==================================================================

        public bool HasAutoPowerOff => _mouse?.HasAutoPowerOff() == true;
        public bool HasLowBatteryWarning => _mouse?.HasLowBatteryWarning() == true;
        public bool HasPower => HasAutoPowerOff || HasLowBatteryWarning;
        public int LowBatteryWarningMax => _mouse?.LowBatteryWarningMax() ?? 50;
        public int LowBatteryWarningStep => Math.Max(1, _mouse?.LowBatteryWarningStep() ?? 10);

        [ObservableProperty]
        private int _powerOff = (int)PowerOffSetting.Never;

        [ObservableProperty]
        private int _lowBatteryWarning;

        private void LoadPower(AsusMouse m)
        {
            PowerOff = (int)m.PowerOffSetting;
            LowBatteryWarning = Math.Clamp(m.LowBatteryWarning, 0, m.LowBatteryWarningMax());
        }

        partial void OnPowerOffChanged(int value)
        {
            if (FromUser && _mouse is { } m) Run(() => m.SetEnergySettings(m.LowBatteryWarning, (PowerOffSetting)value));
        }

        partial void OnLowBatteryWarningChanged(int value)
        {
            if (!FromUser || _mouse is not { } m) return;
            Settle("lowBattery", () =>
            {
                int warning = LowBatteryWarning;
                return () => m.SetEnergySettings(warning, m.PowerOffSetting);
            });
        }

        // ===== Button bindings ========================================================

        public bool HasBindings => _mouse?.HasButtonBindings() == true && _mouse.ButtonBindingsReady && BindingSlots.Count > 0;
        public bool HasSpeedShift => _mouse?.HasSpeedShift == true;

        public ObservableCollection<ButtonBindingViewModel> BindingSlots { get; } = new();

        private readonly List<BindingOption> _bindingOptions = new();
        private ListCollectionView? _bindingOptionsView;

        public ListCollectionView BindingOptions => _bindingOptionsView ??= BindingOptionList.Grouped(_bindingOptions);

        public IReadOnlyList<PeripheralOptionModel> BindingLayerOptions { get; } = new[]
        {
            new PeripheralOptionModel { Value = 0, Label = "Normal" },
            new PeripheralOptionModel { Value = 1, Label = "Speed Shift" },
        };

        /// <summary>0 for the buttons' normal actions, 1 for what they do while Speed Shift is held.</summary>
        [ObservableProperty]
        private int _bindingLayer;

        private void LoadBindings(AsusMouse m)
        {
            BindingSlots.Clear();
            if (!m.HasButtonBindings() || !m.ButtonBindingsReady) return;

            if (_bindingOptions.Count == 0)
            {
                foreach (var (group, items) in m.BindingGroups)
                    foreach (var (code, name) in items)
                        _bindingOptions.Add(new BindingOption(code, name, group));
            }

            bool shift = BindingLayer == 1 && m.HasSpeedShift;
            foreach (var (slot, (_, name)) in m.ButtonSlots)
            {
                if (slot >= m.ButtonBindings.Length) continue;

                // A button that is Speed Shift itself has no second action to choose.
                bool locked = shift && m.ButtonBindings[slot] == AsusMouse.SpeedShiftCode;
                ushort code = shift && !locked ? m.GetSpeedShiftBinding(slot) : m.ButtonBindings[slot];
                EnsureOption(code);
                BindingSlots.Add(new ButtonBindingViewModel(this, slot, name, code, locked));
            }
            BindingOptions.Refresh();
            OnPropertyChanged(nameof(HasBindings));
        }

        /// <summary>
        /// A code the list does not know still gets an entry, so a binding made by
        /// Armoury Crate shows as what it is rather than as a blank box.
        /// </summary>
        private void EnsureOption(ushort code)
        {
            if (_bindingOptions.Any(option => option.Code == code)) return;
            _bindingOptions.Add(new BindingOption(code, AsusMouse.LabelForActionCode(code), "Other"));
        }

        partial void OnBindingLayerChanged(int value)
        {
            if (_mouse is { } m) LoadPartial(() => LoadBindings(m));
        }

        internal void ChooseBinding(ButtonBindingViewModel slot, int code)
        {
            if (!FromUser || _mouse is not { } m) return;

            if (BindingLayer == 1)
            {
                // Speed Shift cannot be its own second action.
                if (code == AsusMouse.SpeedShiftCode)
                {
                    LoadPartial(() => LoadBindings(m));
                    return;
                }
                Run(() => m.SetSpeedShiftBinding(slot.Slot, (ushort)code));
                return;
            }

            Run(() =>
            {
                m.SetButtonBinding(slot.Slot, (ushort)code);
                global::Arsenal.Program.inputDispatcher?.RegisterKeys();
            });
        }

        [RelayCommand]
        private void ResetBindings()
        {
            if (_mouse is not { } m) return;
            Run(() =>
            {
                m.ResetButtonBindings();
                global::Arsenal.Program.inputDispatcher?.RegisterKeys();
            }, () => LoadPartial(() => LoadBindings(m)));
        }
    }

    /// <summary>One DPI stage: the value the mouse switches to when the DPI button cycles onto it.</summary>
    public partial class DpiStageViewModel : ObservableObject
    {
        public int Index { get; }
        public string Label => $"DPI {Index + 1}";
        public bool IsActive { get; }
        public bool CanDelete { get; }

        [ObservableProperty]
        private int _dpi;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Brush))]
        private MediaColor _color;

        public SolidColorBrush Brush => new(Color);

        public DpiStageViewModel(int index, int dpi, MediaColor color, bool isActive, bool canDelete)
        {
            Index = index;
            _dpi = dpi;
            _color = color;
            IsActive = isActive;
            CanDelete = canDelete;
        }
    }

    /// <summary>One mouse button and what it does.</summary>
    public partial class ButtonBindingViewModel : ObservableObject
    {
        private readonly MousePeripheralViewModel _owner;

        public int Slot { get; }
        public string Name { get; }
        public bool IsLocked { get; }
        public bool IsEditable => !IsLocked;

        [ObservableProperty]
        private int _code;

        public ButtonBindingViewModel(MousePeripheralViewModel owner, int slot, string name, int code, bool locked)
        {
            _owner = owner;
            Slot = slot;
            Name = name;
            _code = code;
            IsLocked = locked;
        }

        partial void OnCodeChanged(int value) => _owner.ChooseBinding(this, value);
    }
}
