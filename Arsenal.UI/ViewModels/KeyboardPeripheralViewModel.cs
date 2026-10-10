using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Peripherals;
using Arsenal.Peripherals.Keyboard;
using Arsenal.Peripherals.Keyboard.Models;
using Arsenal.USB;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using PowerOffSetting = Arsenal.Peripherals.Mouse.PowerOffSetting;

namespace Arsenal.UI.ViewModels
{
    /// <summary>
    /// One ASUS keyboard: G-Helper's keyboard window, section for section.
    /// </summary>
    public partial class KeyboardPeripheralViewModel : PeripheralViewModel
    {
        private AsusKeyboard? _keyboard;
        private Azoth? Oled => _keyboard as Azoth;

        /// <summary>Key colours as painted, one per LED. Empty on a keyboard without per-key lighting.</summary>
        private DrawingColor[] _keyColors = Array.Empty<DrawingColor>();

        /// <summary>Set once anything is painted or rebound, so the keyboard is told to keep it.</summary>
        private bool _unsaved;

        public KeyboardPeripheralViewModel(IPeripheralService service, PeripheralDeviceModel device)
            : base(service, device)
        {
            Update(device);
            Load();
        }

        public override SymbolRegular Icon => SymbolRegular.Keyboard24;

        public override void Update(PeripheralDeviceModel device)
        {
            AsusKeyboard? keyboard = Service.FindKeyboard(device.Id);
            if (keyboard is not null && !ReferenceEquals(keyboard, _keyboard))
            {
                if (_keyboard is not null)
                {
                    _keyboard.BatteryUpdated -= OnBatteryUpdated;
                    StopListening();
                }
                _keyboard = keyboard;
                _keyboard.BatteryUpdated += OnBatteryUpdated;
                _keyTiles.Clear();
                SelectedKey = null;
            }
            base.Update(device);
        }

        protected override void StartListening()
        {
            if (_keyboard is not { } k) return;
            k.ProfileChanged -= OnProfileReported;
            k.ProfileChanged += OnProfileReported;
            k.StartEventListener();
        }

        protected override void StopListening()
        {
            if (_keyboard is not { } k) return;
            k.StopEventListener();
            k.ProfileChanged -= OnProfileReported;
        }

        /// <summary>The keyboard's own profile key was pressed.</summary>
        private void OnProfileReported(object? sender, EventArgs e) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_keyboard is not { } k || !k.HasProfiles() || Profile == k.Profile) return;
                Load();
            });

        private void OnBatteryUpdated(object? sender, EventArgs e) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_keyboard is null) return;
                BatteryText = _keyboard.Charging ? $"{_keyboard.Battery}%, charging" : $"{_keyboard.Battery}%";
            });

        protected override string Describe(PeripheralDeviceModel device)
        {
            string kind = device.HasBattery ? "Wireless keyboard" : "Keyboard";
            return HasBattery ? $"{kind}, battery {BatteryText}" : kind;
        }

        protected override void ReadDevice()
        {
            if (_keyboard is not { } k) return;
            k.ReadProfile();
            if (k.HasBattery()) k.ReadBattery();
        }

        protected override void LoadValues()
        {
            if (_keyboard is not { } k) return;

            LoadProfiles(k);
            LoadLighting(k);
            LoadKeys(k);
            LoadPower(k);
            LoadOled();
            NotifyCapabilities();
        }

        private void NotifyCapabilities()
        {
            foreach (string name in new[]
            {
                nameof(HasProfiles), nameof(HasLighting), nameof(MaxBrightness), nameof(HasPerKeyRgb),
                nameof(HasKeyBindings), nameof(HasAutoPowerOff), nameof(HasLowBatteryWarning), nameof(HasPower),
                nameof(LowBatteryWarningMax), nameof(LowBatteryWarningStep), nameof(HasOled),
            })
                OnPropertyChanged(name);
        }

        // ===== Profile ================================================================

        public bool HasProfiles => _keyboard?.HasProfiles() == true;
        public ObservableCollection<PeripheralOptionModel> ProfileOptions { get; } = new();

        [ObservableProperty]
        private int _profile;

        private void LoadProfiles(AsusKeyboard k)
        {
            ProfileOptions.Clear();
            if (!k.HasProfiles()) return;

            // The first slot is the one the keyboard starts in, which G-Helper calls
            // Default; the others are numbered from one.
            ProfileOptions.Add(new PeripheralOptionModel { Value = 0, Label = "Default" });
            for (int i = 1; i < k.ProfileCount(); i++)
                ProfileOptions.Add(new PeripheralOptionModel { Value = i, Label = $"Profile {i}" });
            Profile = Math.Clamp(k.Profile, 0, Math.Max(0, ProfileOptions.Count - 1));
            ReselectAfterRefill(nameof(Profile));
        }

        partial void OnProfileChanged(int value)
        {
            if (Loading || _keyboard is not { } k || k.Profile == value) return;

            DrawingColor[] pending = _keyColors.ToArray();
            bool unsaved = _unsaved;
            _unsaved = false;
            IsReading = true;
            Run(() =>
            {
                // Pending edits belong to the profile being left, so they are kept
                // while it is still the active one.
                if (unsaved)
                {
                    if (pending.Length > 0) k.StoreKeyColors(pending);
                    k.SaveLighting();
                }

                k.SetProfile(value);
                if (k.HasTransientLighting) k.ApplyStoredLighting();
                if (k.HasBattery()) k.ReadBattery();
            }, () => { Load(); IsReading = false; });
        }

        // ===== Lighting ===============================================================

        public bool HasLighting => _keyboard?.HasRGB() == true;
        public int MaxBrightness => _keyboard?.MaxBrightness() ?? 100;

        public ObservableCollection<PeripheralOptionModel> LightingModes { get; } = new();

        public IReadOnlyList<PeripheralOptionModel> SpeedOptions { get; } = Aura.GetSpeeds()
            .Select(pair => new PeripheralOptionModel { Value = (int)pair.Key, Label = pair.Value })
            .ToList();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsManualLighting))]
        [NotifyPropertyChangedFor(nameof(CanPaint))]
        private bool _auraSync;

        public bool IsManualLighting => !AuraSync;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustom))]
        [NotifyPropertyChangedFor(nameof(CanPaint))]
        private int _selectedMode;

        [ObservableProperty]
        private int _speed = (int)AuraSpeed.Normal;

        [ObservableProperty]
        private int _brightness = 100;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Color1Brush))]
        [NotifyPropertyChangedFor(nameof(Color1Hex))]
        private MediaColor _color1 = Colors.Red;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Color2Brush))]
        [NotifyPropertyChangedFor(nameof(Color2Hex))]
        private MediaColor _color2 = Colors.Black;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Color3Brush))]
        [NotifyPropertyChangedFor(nameof(Color3Hex))]
        private MediaColor _color3 = Colors.Black;

        public SolidColorBrush Color1Brush => new(Color1);
        public SolidColorBrush Color2Brush => new(Color2);
        public SolidColorBrush Color3Brush => new(Color3);
        public string Color1Hex => Hex(Color1);
        public string Color2Hex => Hex(Color2);
        public string Color3Hex => Hex(Color3);

        private KeyboardLightingMode Mode => (KeyboardLightingMode)SelectedMode;
        public bool ShowsColor1 => _keyboard?.SupportsColorSetting(Mode) == true;
        public bool ShowsColor2 => _keyboard?.SupportsColor2Setting(Mode) == true;
        public bool ShowsColor3 => _keyboard?.SupportsColor3Setting(Mode) == true;
        public bool ShowsSpeed => _keyboard?.SupportsAnimationSpeed(Mode) == true;

        /// <summary>Custom: each key takes the colour painted on it.</summary>
        public bool IsCustom => Mode == KeyboardLightingMode.Direct;

        public bool CanPaint => IsCustom && !AuraSync;

        private void LoadLighting(AsusKeyboard k)
        {
            AuraSync = PeripheralsProvider.IsKeyboardAuraSync;

            LightingModes.Clear();
            foreach (KeyboardLightingMode mode in k.SupportedLightingModes())
                LightingModes.Add(new PeripheralOptionModel { Value = (int)mode, Label = ModeLabel(mode) });

            KeyboardLightingMode stored = k.StoredMode;
            SelectedMode = (int)(LightingModes.Any(m => m.Value == (int)stored) ? stored
                : LightingModes.Count > 0 ? (KeyboardLightingMode)LightingModes[0].Value : KeyboardLightingMode.Static);
            OnPropertyChanged(nameof(SelectedMode));
            Speed = Math.Clamp((int)k.StoredSpeed, 0, 2);
            Brightness = Math.Clamp(k.StoredBrightness, 0, k.MaxBrightness());
            Color1 = ToMedia(k.StoredColor);
            Color2 = ToMedia(k.StoredColor2);
            Color3 = ToMedia(k.StoredColor3);
            NotifyModeOptions();
        }

        private void NotifyModeOptions()
        {
            OnPropertyChanged(nameof(ShowsColor1));
            OnPropertyChanged(nameof(ShowsColor2));
            OnPropertyChanged(nameof(ShowsColor3));
            OnPropertyChanged(nameof(ShowsSpeed));
        }

        partial void OnAuraSyncChanged(bool value)
        {
            if (Loading || _keyboard is null) return;

            PeripheralsProvider.SetKeyboardAuraSync(value);
            RefreshKeyColors();
            if (value) Run(PeripheralsProvider.SyncKeyboardsWithAura);
            else ApplyLighting();
        }

        partial void OnSelectedModeChanged(int value)
        {
            NotifyModeOptions();
            RefreshKeyColors();
            ApplyLighting();
        }

        partial void OnSpeedChanged(int value) => ApplyLighting();
        partial void OnColor1Changed(MediaColor value) { RefreshKeyColors(); ApplyLighting(); }
        partial void OnColor2Changed(MediaColor value) => ApplyLighting();
        partial void OnColor3Changed(MediaColor value) => ApplyLighting();

        partial void OnBrightnessChanged(int value)
        {
            if (Loading || _keyboard is not { } k) return;

            // Brightness is kept while synced too: the laptop decides the colour, the
            // keyboard still decides how bright to show it.
            Settle("lighting", () =>
            {
                StoreLighting(k);
                return AuraSync ? () => k.SyncFromLaptopAura() : () => { k.ApplyStoredLighting(); k.SaveLighting(); };
            }, 150);
        }

        private void ApplyLighting()
        {
            if (Loading || AuraSync || _keyboard is not { } k) return;

            Settle("lighting", () =>
            {
                StoreLighting(k);
                return () => { k.ApplyStoredLighting(); k.SaveLighting(); };
            }, 150);
        }

        private void StoreLighting(AsusKeyboard k)
        {
            k.StoreLighting(Mode, ToDrawing(Color1), ToDrawing(Color2), ToDrawing(Color3), (AuraSpeed)Speed, Brightness);
            if (_keyColors.Length > 0) k.StoreKeyColors(_keyColors);
        }

        private static string ModeLabel(KeyboardLightingMode mode) => mode switch
        {
            KeyboardLightingMode.Static => "Static",
            KeyboardLightingMode.Breathing => "Breathing",
            KeyboardLightingMode.ColorCycle => "Colour cycle",
            KeyboardLightingMode.Reactive => "Reactive",
            KeyboardLightingMode.Wave => "Rainbow",
            KeyboardLightingMode.Ripple => "Ripple",
            KeyboardLightingMode.StarryNight => "Starry night",
            KeyboardLightingMode.Quicksand => "Quicksand",
            KeyboardLightingMode.Current => "Current",
            KeyboardLightingMode.RainDrop => "Raindrop",
            KeyboardLightingMode.Direct => "Custom",
            _ => mode.ToString(),
        };

        // ===== Keys ===================================================================

        public bool HasPerKeyRgb => _keyboard?.HasPerKeyRGB() == true;
        public bool HasKeyBindings => HasPerKeyRgb && _keyboard?.HasKeyBindings() == true;

        /// <summary>One key width, in device-independent pixels, at which the grid is laid out.</summary>
        private const double KeyUnit = 44;
        private const double KeyGap = 4;

        private readonly ObservableCollection<KeyTileViewModel> _keyTiles = new();
        public ObservableCollection<KeyTileViewModel> KeyTiles => _keyTiles;

        [ObservableProperty]
        private double _keyboardWidth;

        [ObservableProperty]
        private double _keyboardHeight;

        public IReadOnlyList<MediaColor> Palette { get; } = new[]
        {
            MediaColor.FromRgb(0, 0, 0), MediaColor.FromRgb(255, 255, 255),
            MediaColor.FromRgb(255, 0, 0), MediaColor.FromRgb(255, 128, 0), MediaColor.FromRgb(255, 255, 0),
            MediaColor.FromRgb(128, 255, 0), MediaColor.FromRgb(0, 255, 0), MediaColor.FromRgb(0, 255, 255),
            MediaColor.FromRgb(0, 128, 255), MediaColor.FromRgb(0, 0, 255), MediaColor.FromRgb(128, 0, 255),
            MediaColor.FromRgb(255, 0, 255), MediaColor.FromRgb(255, 0, 128), MediaColor.FromRgb(128, 128, 128),
        };

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PaintBrush))]
        [NotifyPropertyChangedFor(nameof(PaintHex))]
        private MediaColor _paintColor = Colors.Red;

        public SolidColorBrush PaintBrush => new(PaintColor);
        public string PaintHex => Hex(PaintColor);

        private void LoadKeys(AsusKeyboard k)
        {
            if (!k.HasPerKeyRGB()) return;

            if (_keyTiles.Count == 0) BuildKeyGrid(k);
            _keyColors = k.StoredKeyColors();
            RefreshKeyColors();
            foreach (KeyTileViewModel tile in _keyTiles)
                tile.IsBound = tile.Usage != 0 && k.GetKeyBinding(tile.Usage) != 0;
            SelectKey(SelectedKey);
        }

        /// <summary>
        /// Lays the keys out from the model's own layout table, as G-Helper does. Keys
        /// that share a legend and sit side by side are one key with several LEDs under
        /// it, such as a split space bar, and are drawn and painted as one.
        /// </summary>
        private void BuildKeyGrid(AsusKeyboard k)
        {
            var rows = new List<KeyDef[]>(k.KeyLayout());
            if (k.MediaKeyCount() > 0)
                rows.Add(Enumerable.Range(1, k.MediaKeyCount()).Select(i => new KeyDef("M" + i)).ToArray());

            int led = 0;
            double width = 0;
            for (int row = 0; row < rows.Count; row++)
            {
                double x = 0;
                KeyTileViewModel? previous = null;
                foreach (KeyDef def in rows[row])
                {
                    x += def.Gap * KeyUnit;
                    string legend = AuraKeyboardLayouts.Legend(def.Name, k.MultiLayout);

                    if (previous is not null && def.Gap == 0 && legend == previous.Label)
                    {
                        previous.Width = x + def.Width * KeyUnit - previous.X - KeyGap;
                        previous.LedSpan++;
                        x += def.Width * KeyUnit;
                        led++;
                        continue;
                    }

                    ushort usage = AuraKeyboardLayouts.Keys.GetValueOrDefault(def.Name);
                    if (usage == 0) usage = AuraKeyboardLayouts.MediaKeys.GetValueOrDefault(def.Name);

                    var tile = new KeyTileViewModel(def.Name, legend, led, usage)
                    {
                        X = x,
                        Y = row * KeyUnit,
                        Width = def.Width * KeyUnit - KeyGap,
                        Height = def.Height * KeyUnit - KeyGap,
                    };
                    _keyTiles.Add(tile);
                    previous = tile;
                    x += def.Width * KeyUnit;
                    led++;
                }
                width = Math.Max(width, x);
            }

            KeyboardWidth = width;
            PaintColor = ToMedia(k.StoredColor);
            KeyboardHeight = rows.Count * KeyUnit;
        }

        /// <summary>What each key shows: its own colour in Custom, the effect's colour otherwise.</summary>
        private void RefreshKeyColors()
        {
            if (_keyTiles.Count == 0) return;
            foreach (KeyTileViewModel tile in _keyTiles)
            {
                tile.Color = AuraSync ? Colors.Gray
                    : IsCustom ? (tile.Led < _keyColors.Length ? ToMedia(_keyColors[tile.Led]) : Colors.Gray)
                    : ShowsColor1 ? Color1
                    : Colors.Gray;
            }
        }

        [RelayCommand]
        private void PickPaint(MediaColor color) => PaintColor = color;

        [RelayCommand]
        private void PressKey(KeyTileViewModel? tile)
        {
            if (tile is null || _keyboard is not { } k) return;

            SelectKey(tile);
            if (!CanPaint) return;

            System.Drawing.Color paint = ToDrawing(PaintColor);
            int first = tile.Led;
            int span = tile.LedSpan;
            bool changed = false;
            for (int i = 0; i < span; i++)
            {
                if (first + i < _keyColors.Length && _keyColors[first + i].ToArgb() != paint.ToArgb())
                {
                    _keyColors[first + i] = paint;
                    changed = true;
                }
            }
            if (!changed) return;

            tile.Color = PaintColor;
            _unsaved = true;

            // The whole map goes each time, as upstream does: a keyboard painted one
            // colour all over is then sent as a static effect instead of key by key.
            System.Drawing.Color[] colors = (System.Drawing.Color[])_keyColors.Clone();
            Run(() => k.SetLedColors(colors));
            ScheduleSave();
        }

        [RelayCommand]
        private void FillAll()
        {
            if (!CanPaint || _keyboard is not { } k || _keyColors.Length == 0) return;

            Array.Fill(_keyColors, ToDrawing(PaintColor));
            RefreshKeyColors();
            _unsaved = true;
            DrawingColor[] colors = _keyColors.ToArray();
            Run(() => k.SetLedColors(colors));
            ScheduleSave();
        }

        /// <summary>
        /// Keeps painted colours once painting stops. G-Helper saves when its window
        /// closes; a page has no such moment, so this waits for a pause instead.
        /// </summary>
        private void ScheduleSave()
        {
            if (_keyboard is not { } k) return;
            Settle("save", () =>
            {
                if (!_unsaved) return null;
                _unsaved = false;
                DrawingColor[] colors = _keyColors.ToArray();
                return () =>
                {
                    if (colors.Length > 0) k.StoreKeyColors(colors);
                    k.SaveLighting();
                };
            }, 1500);
        }

        // ===== Key bindings ===========================================================

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedKeyTitle))]
        [NotifyPropertyChangedFor(nameof(CanBindSelectedKey))]
        private KeyTileViewModel? _selectedKey;

        public string SelectedKeyTitle => SelectedKey is null ? "Select a key above" : $"Key {SelectedKey.Label}";
        public bool CanBindSelectedKey => SelectedKey is { Usage: > 0 };

        private List<BindingOption>? _keyBindingOptions;
        private ListCollectionView? _keyBindingOptionsView;

        public ListCollectionView KeyBindingOptions =>
            _keyBindingOptionsView ??= BindingOptionList.Grouped(_keyBindingOptions ??= BuildKeyBindingOptions());

        /// <summary>The action on the selected key; 0 while none is selected.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLaunchBinding))]
        private int _keyBinding;

        [ObservableProperty]
        private string _launchCommand = string.Empty;

        public bool IsLaunchBinding => LaunchSlot(KeyBinding) >= 0;

        private bool _selectingKey;

        private void SelectKey(KeyTileViewModel? tile)
        {
            if (SelectedKey is not null) SelectedKey.IsSelected = false;
            SelectedKey = tile;
            if (tile is null || _keyboard is not { } k) return;
            tile.IsSelected = true;

            _selectingKey = true;
            try
            {
                ushort current = tile.Usage == 0 ? (ushort)0 : k.GetKeyBinding(tile.Usage);
                if (current == 0) current = tile.Usage;
                KeyBinding = current;
                int slot = LaunchSlot(current);
                LaunchCommand = slot >= 0 ? AsusKeyboard.LaunchCommand(slot) : string.Empty;
            }
            finally { _selectingKey = false; }
        }

        partial void OnKeyBindingChanged(int value)
        {
            if (_selectingKey || Loading || SelectedKey is not { Usage: > 0 } tile || _keyboard is not { } k) return;

            BindingOption? option = _keyBindingOptions?.FirstOrDefault(o => o.Code == value);
            if (option is null) return;

            int slot = LaunchSlot(value);
            _selectingKey = true;
            try { LaunchCommand = slot >= 0 ? AsusKeyboard.LaunchCommand(slot) : string.Empty; }
            finally { _selectingKey = false; }

            ushort source = tile.Usage;
            Run(() =>
            {
                if (option.ComboKeys is null) k.SetKeyBinding(source, (ushort)option.Code);
                else k.SetKeyCombo(source, (ushort)option.Code, option.ComboKeys);
                k.SaveLighting();
            }, () => tile.IsBound = k.GetKeyBinding(source) != 0);
        }

        partial void OnLaunchCommandChanged(string value)
        {
            if (_selectingKey || Loading) return;
            int slot = LaunchSlot(KeyBinding);
            if (slot < 0) return;

            AsusKeyboard.SetLaunchCommand(slot, value);
            Settle("launch", () => () => global::Arsenal.Program.inputDispatcher?.RegisterKeys(), 600);
        }

        [RelayCommand]
        private void ResetKeyBindings()
        {
            if (_keyboard is not { } k) return;
            Run(k.ResetAllBindings, () =>
            {
                foreach (KeyTileViewModel tile in _keyTiles)
                    tile.IsBound = tile.Usage != 0 && k.GetKeyBinding(tile.Usage) != 0;
                SelectKey(SelectedKey);
            });
        }

        private static int LaunchSlot(int code)
        {
            int slot = code - AsusKeyboard.LaunchCode;
            return slot >= 0 && slot < AsusKeyboard.LaunchSlots ? slot : -1;
        }

        private static ushort Key(string name) => AuraKeyboardLayouts.Keys[name];

        /// <summary>
        /// Shortcuts a key can send in one press. The codes are this list's own, never
        /// sent to the keyboard; the keys behind them are. The same set as G-Helper.
        /// </summary>
        private static readonly (ushort Code, string Name, ushort[] Keys)[] KeyCombos =
        {
            (0xC101, "Copy", new[] { Key("Ctrl"), Key("C") }),
            (0xC102, "Paste", new[] { Key("Ctrl"), Key("V") }),
            (0xC103, "Cut", new[] { Key("Ctrl"), Key("X") }),
            (0xC104, "Undo", new[] { Key("Ctrl"), Key("Z") }),
            (0xC105, "Select all", new[] { Key("Ctrl"), Key("A") }),
            (0xC106, "Alt + Tab", new[] { Key("Alt"), Key("Tab") }),
            (0xC107, "Alt + F4", new[] { Key("Alt"), Key("F4") }),
            (0xC108, "Show desktop", new[] { Key("Win"), Key("D") }),
            (0xC109, "Lock PC", new[] { Key("Win"), Key("L") }),
            (0xC10A, "Task Manager", new[] { Key("Ctrl"), Key("Shift"), Key("Esc") }),
            (0xC10B, "Screenshot", new[] { Key("Win"), Key("PrtSc") }),
            (0xC10C, "Snipping Tool", new[] { Key("Win"), Key("Shift"), Key("S") }),
        };

        private List<BindingOption> BuildKeyBindingOptions()
        {
            var options = new List<BindingOption>();
            Dictionary<string, string>? region = _keyboard is null ? null
                : AuraKeyboardLayouts.RegionalLegends.GetValueOrDefault(_keyboard.MultiLayout);

            foreach (var (name, code) in AuraKeyboardLayouts.Keys)
                options.Add(new BindingOption(code, region?.GetValueOrDefault(name) ?? name, "Keyboard"));
            foreach (var (name, code) in AuraKeyboardLayouts.MultimediaTargets)
                options.Add(new BindingOption(code, name, "Multimedia"));
            foreach (var (name, code) in AuraKeyboardLayouts.MouseTargets)
                options.Add(new BindingOption(code, name, "Mouse"));
            foreach (var (code, name, keys) in KeyCombos)
                options.Add(new BindingOption(code, name, "Shortcuts", keys));
            for (int slot = 0; slot < AsusKeyboard.LaunchSlots; slot++)
            {
                options.Add(new BindingOption(AsusKeyboard.LaunchCode + slot, $"Launch app {slot + 1}", "Launch",
                    new[] { Key("Ctrl"), Key("Shift"), Key("Alt"), Key("F" + (slot + 1)) }));
            }

            // One entry per code: the list binds by code, and two entries with the
            // same one would select whichever came first.
            return options.DistinctBy(option => option.Code).ToList();
        }

        // ===== Power ==================================================================

        public bool HasAutoPowerOff => _keyboard?.HasAutoPowerOff() == true;
        public bool HasLowBatteryWarning => _keyboard?.HasLowBatteryWarning() == true;
        public bool HasPower => HasAutoPowerOff || HasLowBatteryWarning;
        public int LowBatteryWarningMax => _keyboard?.LowBatteryWarningMax() ?? 50;
        public int LowBatteryWarningStep => Math.Max(1, _keyboard?.LowBatteryWarningStep() ?? 10);

        [ObservableProperty]
        private int _powerOff = (int)PowerOffSetting.Never;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LowBatteryWarningText))]
        private int _lowBatteryWarning;

        public string LowBatteryWarningText => LowBatteryWarning == 0 ? "Never" : $"{LowBatteryWarning}%";

        private void LoadPower(AsusKeyboard k)
        {
            PowerOff = (int)k.PowerOffSetting;
            LowBatteryWarning = Math.Clamp(k.LowBatteryWarning, 0, k.LowBatteryWarningMax());
        }

        partial void OnPowerOffChanged(int value)
        {
            if (!Loading && _keyboard is { } k) Run(() => k.SetEnergySettings(k.LowBatteryWarning, (PowerOffSetting)value));
        }

        partial void OnLowBatteryWarningChanged(int value)
        {
            if (Loading || _keyboard is not { } k) return;
            Settle("lowBattery", () =>
            {
                int step = Math.Max(1, k.LowBatteryWarningStep());
                int warning = (int)Math.Round(Math.Clamp(LowBatteryWarning, 0, k.LowBatteryWarningMax()) / (double)step) * step;
                return () => k.SetEnergySettings(warning, k.PowerOffSetting);
            });
        }

        // ===== Screen =================================================================

        public bool HasOled => Oled is not null;
        public ObservableCollection<PeripheralOptionModel> OledModes { get; } = new();

        [ObservableProperty]
        private int _oledMode;

        [ObservableProperty]
        private int _oledBrightness = 100;

        private void LoadOled()
        {
            OledModes.Clear();
            if (Oled is not { } oled) return;

            int count = oled.OledAnimationCount();
            OledModes.Add(new PeripheralOptionModel { Value = 0, Label = "Off" });
            for (int i = 1; i <= count; i++)
                OledModes.Add(new PeripheralOptionModel { Value = i, Label = $"Animation {i}" });
            OledModes.Add(new PeripheralOptionModel { Value = count + 1, Label = "Clock" });

            OledBrightness = oled.OledBrightness < 0 ? 100 : Math.Clamp(oled.OledBrightness, 0, 100);
            OledMode = !oled.OledEnabled ? 0
                : oled.OledClock ? count + 1
                : Math.Clamp(Math.Max(oled.OledAnimation, 0) + 1, 1, Math.Max(1, count));
            ReselectAfterRefill(nameof(OledMode));
        }

        partial void OnOledModeChanged(int value)
        {
            if (Loading || Oled is not { } oled) return;

            int count = oled.OledAnimationCount();
            Run(() =>
            {
                if (value <= 0)
                {
                    oled.SetOledClock(false);
                    oled.SetOledEnabled(false);
                    return;
                }

                if (!oled.OledEnabled) oled.SetOledEnabled(true);
                if (value > count) oled.SetOledClock(true);
                else
                {
                    oled.SetOledClock(false);
                    oled.SetOledAnimation(value - 1);
                }
            });
        }

        partial void OnOledBrightnessChanged(int value)
        {
            if (Loading || Oled is not { } oled) return;
            Settle("oledBrightness", () =>
            {
                int brightness = OledBrightness;
                return () => oled.SetOledBrightness(brightness);
            });
        }
    }

    /// <summary>One key on the keyboard drawing.</summary>
    public partial class KeyTileViewModel : ObservableObject
    {
        public string Name { get; }
        public string Label { get; }
        public int Led { get; }

        /// <summary>The key's HID usage, or 0 for a key that cannot be rebound.</summary>
        public ushort Usage { get; }

        /// <summary>How many LEDs sit under this key, for a split key drawn as one.</summary>
        public int LedSpan { get; set; } = 1;

        public double X { get; init; }
        public double Y { get; init; }

        [ObservableProperty]
        private double _width;

        public double Height { get; init; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Brush))]
        private MediaColor _color = Colors.Gray;

        public SolidColorBrush Brush => new(Color);

        [ObservableProperty]
        private bool _isSelected;

        /// <summary>The key has been given another action, which the drawing underlines.</summary>
        [ObservableProperty]
        private bool _isBound;

        public string ToolTip => $"{Name} (LED {Led})";

        public KeyTileViewModel(string name, string label, int led, ushort usage)
        {
            Name = name;
            Label = label;
            Led = led;
            Usage = usage;
        }
    }
}
