using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Services.Contracts;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;
using MediaColor = System.Windows.Media.Color;

namespace Arsenal.UI.ViewModels
{
    public partial class SelectableIntOption : ObservableObject
    {
        public int Value { get; }
        public string Name { get; }
        [ObservableProperty] private bool _isSelected;

        public SelectableIntOption(int value, string name, bool isSelected = false)
        {
            Value = value;
            Name = name;
            IsSelected = isSelected;
        }
    }

    public partial class LightingViewModel : ObservableObject
    {
        private readonly ILightingService _lightingService;
        private bool _isReady;

        [ObservableProperty]
        private int _brightness = 2;

        [ObservableProperty]
        private int _selectedMode = 0; // Static, Breathing, Color Cycle, Rainbow, etc.

        [ObservableProperty]
        private byte _red = 255;

        [ObservableProperty]
        private byte _green = 0;

        [ObservableProperty]
        private byte _blue = 128;

        [ObservableProperty]
        private MediaColor _selectedColor = MediaColor.FromRgb(255, 0, 128);

        public string HexColor => $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
        public string RgbColor => $"RGB {SelectedColor.R}, {SelectedColor.G}, {SelectedColor.B}";
        public System.Windows.Media.SolidColorBrush ColorPreviewBrush => new(SelectedColor);

        [ObservableProperty]
        private int _speed = 1; // 0=Slow, 1=Normal, 2=Fast

        [ObservableProperty]
        private bool _awake = true;

        [ObservableProperty]
        private bool _boot = true;

        [ObservableProperty]
        private bool _sleep = false;

        [ObservableProperty]
        private bool _shutdown = false;

        /// <summary>
        /// Idle seconds before the keyboard backlight goes out, kept separately for
        /// battery and for AC because the reason to have one differs: saving charge on
        /// battery, and only taste on the wall. Zero leaves the backlight on.
        /// </summary>
        [ObservableProperty] private int _keyboardTimeoutSeconds;
        [ObservableProperty] private bool _useAmbientBacklight;

        public bool HasAmbientLightSensor { get; }

        partial void OnUseAmbientBacklightChanged(bool value)
        {
            if (!_isReady) return;
            AppConfig.Set("backlight_ambient", value ? 1 : 0);
            Input.AmbientLight.ApplySetting();
            if (!value) Input.InputDispatcher.SetBacklightAuto();
        }

        [ObservableProperty] private int _keyboardAcTimeoutSeconds;

        /// <summary>
        /// The track has to reach whatever is already stored, or opening the page would
        /// clamp a longer delay down to the end of a slider that cannot draw it.
        /// </summary>
        public int KeyboardTimeoutMaximum { get; private set; } = 600;

        public int KeyboardAcTimeoutMaximum { get; private set; } = 600;

        [ObservableProperty] private bool _hasAnimeMatrix;
        [ObservableProperty] private bool _hasSlash;
        [ObservableProperty] private bool _hasMatrixOrSlash;

        /// <summary>
        /// Addressable LEDs behind the Slash bar on this chassis, which the preview
        /// has to draw and the device has to be sent. Read from the same place
        /// <see cref="Arsenal.AnimeMatrix.SlashDevice"/> reads it, so the two cannot
        /// end up disagreeing about the hardware.
        /// </summary>
        [ObservableProperty] private int _slashSegments = 7;
        [ObservableProperty] private bool _hasKeyboardColor = true;
        [ObservableProperty] private bool _hasAuraEffects = true;
        [ObservableProperty] private int _matrixBrightness;
        [ObservableProperty] private int _matrixMode;
        [ObservableProperty] private bool _matrixDisableOnBattery;
        [ObservableProperty] private bool _matrixDisableWithLid;
        [ObservableProperty] private bool _matrixFlip;
        [ObservableProperty] private string _matrixText = "Hello!";
        [ObservableProperty] private string _matrixText2 = string.Empty;
        [ObservableProperty] private int _matrixTextFont;
        [ObservableProperty] private int _matrixTextFont2;
        [ObservableProperty] private int _matrixTextSize = 15;
        [ObservableProperty] private int _matrixTextSize2 = 15;
        [ObservableProperty] private bool _matrixTextRunning;
        [ObservableProperty] private string _matrixTimeFormat = "HH:mm";
        [ObservableProperty] private string _matrixDateFormat = "yy.MM.dd";
        [ObservableProperty] private bool _matrixClockBattery;
        [ObservableProperty] private int _matrixAudioMode;
        [ObservableProperty] private string _lightingDeviceTitle = "Anime Matrix";

        /// <summary>
        /// The glyph beside the heading, naming the device the same way the title does:
        /// a dot grid for the AniMe Matrix, a diagonal stroke for the Slash bar.
        /// </summary>
        [ObservableProperty] private SymbolRegular _lightingDeviceIcon = SymbolRegular.GridDots24;
        [ObservableProperty] private int _slashInterval;
        [ObservableProperty] private bool _slashBootAnimation;
        [ObservableProperty] private bool _slashSleepAnimation;
        [ObservableProperty] private int _slashSleepPattern;
        [ObservableProperty] private bool _slashLowBatteryAlert;
        [ObservableProperty] private bool _slashBatteryIndicator;
        [ObservableProperty] private bool _slashPowerSaving;
        [ObservableProperty] private int _slashDimLevel = 20;


        // ------------------------------------------------------------------
        // Keyboard preview
        // ------------------------------------------------------------------

        /// <summary>The <c>AuraBacklightType</c> the preview draws for.</summary>
        [ObservableProperty] private int _backlightZoneType;

        /// <summary>The second Aura colour, which Breathe fades to.</summary>
        [ObservableProperty] private MediaColor _secondaryColor = MediaColor.FromRgb(0, 0, 0);

        [ObservableProperty] private bool _previewNumpad;
        [ObservableProperty] private bool _previewIso;

        /// <summary>
        /// Preview-only appearance switch. It never changes the hardware command: it
        /// describes whether light is visible around the physical keycaps.
        /// </summary>
        [ObservableProperty] private bool _previewKeyEdgeLighting = true;

        /// <summary>The chassis the layout was matched to, empty when nothing matched.</summary>
        [ObservableProperty] private string _previewChassis = string.Empty;

        /// <summary>
        /// The whole shape handed to the preview: what the chassis table says, with the
        /// two things the user can correct applied over the top.
        /// </summary>
        [ObservableProperty] private Arsenal.Peripherals.Keyboard.LaptopKeyboardOptions _previewLayout;

        private Arsenal.Peripherals.Keyboard.LaptopKeyboardOptions _chassisLayout;

        /// <summary>
        /// Shown under the preview so it is clear what is being drawn and why, since
        /// the picture cannot say on its own whether it is mirroring the keyboard or
        /// reproducing it.
        /// </summary>
        public string PreviewCaption => BacklightZoneType switch
        {
            (int)Arsenal.USB.AuraBacklightType.PerKey =>
                "Your keyboard lights every key on its own. The effect is reproduced here from its shape, colour and speed. The keyboard plays it in its own firmware and does not report where in the animation it is, so the two will not be in step.",
            (int)Arsenal.USB.AuraBacklightType.MultiZone =>
                "Your keyboard lights in four zones, so the preview shows four bands rather than a colour per key. The effect is reproduced from its shape, colour and speed; the keyboard plays it in its own firmware and will not be in step.",
            _ => "Your keyboard lights as one zone, so every key shows the same colour. The effect is reproduced from its shape, colour and speed; the keyboard plays it in its own firmware and will not be in step.",
        };

        /// <summary>
        /// Says where the drawn shape came from - the chassis it was matched to, or
        /// that nothing matched and this is a generic shape. Without it the preview
        /// silently implies a precision the table may not have for this model.
        /// </summary>
        public string PreviewShapeSource => PreviewChassis.Length > 0
            ? string.Format("Matched to {0}. Nothing on the machine reports whether it has a number pad, so correct it here if this is wrong. Your choice is remembered.", PreviewChassis)
            : "This chassis is not in Arsenal's keyboard table, so the shape is a generic ROG laptop. Set it here and your choice is remembered.";

        /// <summary>
        /// The preview row carries the colour picker beside the keyboard, so it has to
        /// survive either capability on its own.
        /// </summary>
        /// <remarks>
        /// The two are read from different places - a white-only backlight has no
        /// colour, a machine configured with no Aura has no effects - and a machine can
        /// have one without the other. Binding the row to the effects flag alone would
        /// take the colour picker off a machine that has a colour to set.
        /// </remarks>
        public bool HasKeyboardPreviewOrColor => HasAuraEffects || HasKeyboardColor;

        partial void OnBacklightZoneTypeChanged(int value) => OnPropertyChanged(nameof(PreviewCaption));

        partial void OnHasAuraEffectsChanged(bool value) => OnPropertyChanged(nameof(HasKeyboardPreviewOrColor));

        partial void OnHasKeyboardColorChanged(bool value) => OnPropertyChanged(nameof(HasKeyboardPreviewOrColor));

        partial void OnPreviewChassisChanged(string value) => OnPropertyChanged(nameof(PreviewShapeSource));

        private void LoadPreviewLayout()
        {
            BacklightZoneType = _lightingService.BacklightZoneType;

            // The chassis table decides the arrangement, the hardware decides the
            // region and the light bar, and a saved answer is the user's own
            // correction of the two the table can be wrong about.
            var match = Arsenal.Peripherals.Keyboard.LaptopKeyboardLayout.Detect();
            _chassisLayout = match.Options with { Lightbar = _lightingService.HasLightbar };
            PreviewChassis = match.ChassisName;
            PreviewNumpad = AppConfig.Get("keyboard_numpad", _chassisLayout.Numpad ? 1 : 0) == 1;
            PreviewIso = AppConfig.Get("keyboard_iso", _chassisLayout.Iso ? 1 : 0) == 1;
            PreviewKeyEdgeLighting = AppConfig.Get(
                "keyboard_preview_key_edge_lighting",
                match.KeyEdgeLightingByDefault ? 1 : 0) == 1;
            ApplyPreviewLayout();
            SecondaryColor = FromArgbInt(AppConfig.Get("aura_color2", 0));
        }

        private void ApplyPreviewLayout()
            => PreviewLayout = _chassisLayout with { Numpad = PreviewNumpad, Iso = PreviewIso };

        private static MediaColor FromArgbInt(int value) => MediaColor.FromRgb(
            (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));

        partial void OnPreviewNumpadChanged(bool value)
        {
            ApplyPreviewLayout();
            if (_isReady) AppConfig.Set("keyboard_numpad", value ? 1 : 0);
        }

        partial void OnPreviewIsoChanged(bool value)
        {
            ApplyPreviewLayout();
            if (_isReady) AppConfig.Set("keyboard_iso", value ? 1 : 0);
        }

        partial void OnPreviewKeyEdgeLightingChanged(bool value)
        {
            if (_isReady) AppConfig.Set("keyboard_preview_key_edge_lighting", value ? 1 : 0);
        }
        public ObservableCollection<SelectableIntOption> AuraModes { get; } = new();
        public ObservableCollection<SelectableIntOption> MatrixModes { get; } = new();
        public IReadOnlyList<int> SlashDimLevels { get; } = new[] { 10, 20, 30, 40, 50, 100 };
        public IReadOnlyList<string> MatrixFonts { get; } = Arsenal.AnimeMatrix.AniMatrixControl.TextFonts
            .Select(name => string.IsNullOrEmpty(name) ? "System default" : name).ToArray();

        public LightingViewModel(ILightingService lightingService)
        {
            _lightingService = lightingService;
            Brightness = _lightingService.Brightness;
            SelectedMode = _lightingService.CurrentMode;
            int savedColor = AppConfig.Get("aura_color", 0xFF0080);
            SelectedColor = MediaColor.FromRgb(
                (byte)((savedColor >> 16) & 0xFF),
                (byte)((savedColor >> 8) & 0xFF),
                (byte)(savedColor & 0xFF));
            Speed = Math.Clamp(AppConfig.Get("aura_speed", 1), 0, 2);
            Awake = AppConfig.IsNotFalse("keyboard_awake");
            Boot = AppConfig.IsNotFalse("keyboard_boot");
            Sleep = AppConfig.IsNotFalse("keyboard_sleep");
            Shutdown = AppConfig.IsNotFalse("keyboard_shutdown");
            HasAnimeMatrix = _lightingService.HasAnimeMatrix;
            HasSlash = _lightingService.HasSlash;
            HasMatrixOrSlash = HasAnimeMatrix || HasSlash;

            // A white-only backlight takes a colour and throws it away. Showing the
            // picker there is the same lie as showing a MUX switch on a machine
            // without one, and the legacy client hid it for exactly this reason.
            HasKeyboardColor = _lightingService.HasKeyboardColor;
            HasAuraEffects = _lightingService.HasAuraEffects;
            MatrixBrightness = _lightingService.MatrixBrightness;
            MatrixMode = _lightingService.MatrixMode;
            foreach (var pair in Arsenal.USB.Aura.GetModes())
                AuraModes.Add(new SelectableIntOption((int)pair.Key, pair.Value, (int)pair.Key == SelectedMode));

            if (HasSlash)
            {
                LightingDeviceTitle = "Slash lighting";
                LightingDeviceIcon = SymbolRegular.Line24;
                SlashSegments = AppConfig.IsSlashLong() ? 35 : 7;
                foreach (var pair in Arsenal.AnimeMatrix.SlashDevice.Modes)
                    MatrixModes.Add(new SelectableIntOption((int)pair.Key, pair.Value, (int)pair.Key == MatrixMode));
                LoadSlashSettings();
            }
            else
            {
                string[] matrixNames = { "Banner", "Logo", "Picture", "Clock", "Audio", "Text" };
                for (int i = 0; i < matrixNames.Length; i++)
                    MatrixModes.Add(new SelectableIntOption(i, matrixNames[i], i == MatrixMode));
            }
            MatrixDisableOnBattery = AppConfig.Is("matrix_auto");
            MatrixDisableWithLid = AppConfig.Is("matrix_lid");
            MatrixFlip = AppConfig.Is("matrix_flip");
            MatrixText = AppConfig.GetString("matrix_text", "Hello!");
            MatrixText2 = AppConfig.GetString("matrix_text2", string.Empty);
            MatrixTextFont = Math.Clamp(AppConfig.Get("matrix_text_font", 0), 0, MatrixFonts.Count - 1);
            MatrixTextFont2 = Math.Clamp(AppConfig.Get("matrix_text2_font", 0), 0, MatrixFonts.Count - 1);
            MatrixTextSize = Math.Clamp(AppConfig.Get("matrix_text_size", 15), 8, 36);
            MatrixTextSize2 = Math.Clamp(AppConfig.Get("matrix_text2_size", 15), 8, 36);
            MatrixTextRunning = AppConfig.Is("matrix_text_running");
            MatrixTimeFormat = AppConfig.GetString("matrix_time", "HH:mm");
            MatrixDateFormat = AppConfig.GetString("matrix_date", "yy.MM.dd");
            MatrixClockBattery = AppConfig.Is("matrix_clock_battery");
            MatrixAudioMode = Math.Clamp(AppConfig.Get("matrix_audio_mode", 0), 0, 1);
            _lightingService.BrightnessChanged += level => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Brightness = level);
            _lightingService.ModeChanged += mode => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                SelectedMode = mode;
                UpdateSelection(AuraModes, mode);
            });
            HasAmbientLightSensor = Input.AmbientLight.IsSupported();
            UseAmbientBacklight = AppConfig.Is("backlight_ambient");
            KeyboardTimeoutSeconds = Math.Max(0, AppConfig.Get("keyboard_timeout", 60));
            KeyboardAcTimeoutSeconds = Math.Max(0, AppConfig.Get("keyboard_ac_timeout", 0));

            // Widened before the page binds, so nothing already in force is out of reach.
            KeyboardTimeoutMaximum = Math.Max(KeyboardTimeoutMaximum, KeyboardTimeoutSeconds);
            KeyboardAcTimeoutMaximum = Math.Max(KeyboardAcTimeoutMaximum, KeyboardAcTimeoutSeconds);
            LoadPreviewLayout();
            _isReady = true;
        }

        [RelayCommand]
        public void SetBrightnessLevel(object? levelParam)
        {
            int level = ToInt(levelParam, 2);
            Brightness = level;
            _lightingService.SetBrightness(level);
        }

        [RelayCommand]
        public void SetAuraMode(object? modeParam)
        {
            int mode = ToInt(modeParam, 0);
            SelectedMode = mode;
            UpdateSelection(AuraModes, mode);
            _lightingService.SetMode(mode);
        }

        [RelayCommand]
        public void ApplyColor()
        {
            _lightingService.SetColor(SelectedColor.R, SelectedColor.G, SelectedColor.B);
            _lightingService.SetSpeed(Speed);
        }

        [RelayCommand]
        public void SetPresetColor(object? colorParam)
        {
            if (colorParam is string value && System.Windows.Media.ColorConverter.ConvertFromString(value) is MediaColor color)
                SelectedColor = color;
        }

        [RelayCommand]
        public void SetSpeed(object? speedParam) => Speed = Math.Clamp(ToInt(speedParam, 1), 0, 2);

        partial void OnSelectedColorChanged(MediaColor value)
        {
            Red = value.R;
            Green = value.G;
            Blue = value.B;
            OnPropertyChanged(nameof(HexColor));
            OnPropertyChanged(nameof(RgbColor));
            OnPropertyChanged(nameof(ColorPreviewBrush));
        }

        partial void OnAwakeChanged(bool value)
        {
            if (_isReady) _lightingService.SetAwake(value);
        }

        partial void OnBootChanged(bool value)
        {
            if (_isReady) _lightingService.SetBoot(value);
        }

        partial void OnSleepChanged(bool value)
        {
            if (_isReady) _lightingService.SetSleep(value);
        }

        partial void OnShutdownChanged(bool value)
        {
            if (_isReady) _lightingService.SetShutdown(value);
        }

        partial void OnKeyboardTimeoutSecondsChanged(int value) { if (!_isReady) return; AppConfig.Set("keyboard_timeout", Math.Max(0, value)); Program.inputDispatcher?.InitBacklightTimer(); }
        partial void OnKeyboardAcTimeoutSecondsChanged(int value) { if (!_isReady) return; AppConfig.Set("keyboard_ac_timeout", Math.Max(0, value)); Program.inputDispatcher?.InitBacklightTimer(); }

        partial void OnMatrixDisableOnBatteryChanged(bool value)
        {
            if (_isReady) _lightingService.SetMatrixPowerPolicy(value, MatrixDisableWithLid);
        }

        partial void OnMatrixDisableWithLidChanged(bool value)
        {
            if (_isReady) _lightingService.SetMatrixPowerPolicy(MatrixDisableOnBattery, value);
        }

        partial void OnMatrixFlipChanged(bool value)
        {
            if (!_isReady) return;
            AppConfig.Set("matrix_flip", value ? 1 : 0);
            Program.matrixControl?.deviceMatrix?.PresentClock();
        }

        [RelayCommand]
        public void UpdatePowerStates()
        {
            _lightingService.SetAwake(Awake);
            _lightingService.SetBoot(Boot);
            _lightingService.SetSleep(Sleep);
            _lightingService.SetShutdown(Shutdown);
        }

        [RelayCommand]
        public void SetMatrixBrightnessLevel(object? valueParam)
        {
            MatrixBrightness = Math.Clamp(ToInt(valueParam, MatrixBrightness), 0, 3);
            _lightingService.SetMatrixBrightness(MatrixBrightness);
        }

        [RelayCommand]
        public void SetMatrixRunningMode(object? valueParam)
        {
            MatrixMode = ToInt(valueParam, MatrixMode);
            UpdateSelection(MatrixModes, MatrixMode);
            _lightingService.SetMatrixMode(MatrixMode);
        }

        [RelayCommand]
        public void ChooseMatrixPicture()
        {
            Program.matrixControl?.OpenMatrixPicture();
            MatrixMode = (int)Arsenal.AnimeMatrix.MatrixMode.Picture;
            UpdateSelection(MatrixModes, MatrixMode);
        }

        [RelayCommand]
        public void ApplyMatrixText()
        {
            AppConfig.Set("matrix_text", MatrixText);
            AppConfig.Set("matrix_text2", MatrixText2);
            AppConfig.Set("matrix_text_font", MatrixTextFont);
            AppConfig.Set("matrix_text2_font", MatrixTextFont2);
            AppConfig.Set("matrix_text_size", Math.Clamp(MatrixTextSize, 8, 36));
            AppConfig.Set("matrix_text2_size", Math.Clamp(MatrixTextSize2, 8, 36));
            AppConfig.Set("matrix_text_running", MatrixTextRunning ? 1 : 0);
            ActivateMatrixMode(Arsenal.AnimeMatrix.MatrixMode.Text, () => Program.matrixControl?.SetMatrixText());
        }

        [RelayCommand]
        public void ApplyMatrixClock()
        {
            try { _ = DateTime.Now.ToString(MatrixTimeFormat); if (MatrixTimeFormat.Length > 0) AppConfig.Set("matrix_time", MatrixTimeFormat); } catch { }
            try { _ = DateTime.Now.ToString(MatrixDateFormat); if (MatrixDateFormat.Length > 0) AppConfig.Set("matrix_date", MatrixDateFormat); } catch { }
            AppConfig.Set("matrix_clock_battery", MatrixClockBattery ? 1 : 0);
            ActivateMatrixMode(Arsenal.AnimeMatrix.MatrixMode.Clock, () =>
            {
                Program.matrixControl?.SetMatrixClock();
                Program.matrixControl?.deviceMatrix?.PresentClock();
            });
        }

        [RelayCommand]
        public void ApplyMatrixAudio()
        {
            AppConfig.Set("matrix_audio_mode", Math.Clamp(MatrixAudioMode, 0, 1));
            ActivateMatrixMode(Arsenal.AnimeMatrix.MatrixMode.Audio, () => Program.matrixControl?.SetDevice());
        }

        private void ActivateMatrixMode(Arsenal.AnimeMatrix.MatrixMode mode, Action refresh)
        {
            int value = (int)mode;
            if (MatrixMode == value) refresh();
            else
            {
                MatrixMode = value;
                UpdateSelection(MatrixModes, MatrixMode);
                _lightingService.SetMatrixMode(MatrixMode);
            }
        }

        [RelayCommand]
        public void UpdateMatrixPowerPolicy() => _lightingService.SetMatrixPowerPolicy(MatrixDisableOnBattery, MatrixDisableWithLid);

        private void LoadSlashSettings()
        {
            SlashInterval = Math.Clamp(AppConfig.Get("matrix_interval", 0), 0, 5);
            SlashDimLevel = AppConfig.Get("slash_dim", 20);
            var slash = Program.matrixControl?.deviceSlash;
            if (slash == null) return;
            try
            {
                SlashBootAnimation = slash.GetFlag(0xA0);
                byte[]? sleep = slash.GetRecord(0xA1);
                SlashSleepAnimation = sleep is not null && sleep[8] == 0x01;
                SlashSleepPattern = sleep is not null && sleep[6] != 0x00 ? 1 : 0;
                SlashLowBatteryAlert = slash.GetFlag(0xA2);
                SlashBatteryIndicator = slash.GetFlag(0xA3);
                SlashPowerSaving = slash.GetFlag(0xA8);
            }
            catch { }
        }

        partial void OnSlashIntervalChanged(int value)
        {
            if (!_isReady) return;
            AppConfig.Set("matrix_interval", Math.Clamp(value, 0, 5));
            Program.matrixControl?.SetDevice();
        }

        partial void OnSlashBootAnimationChanged(bool value) => WithSlash(s => { s.SetFlag(0xA0, value); s.SetFlag(0xA4, value); });
        partial void OnSlashSleepAnimationChanged(bool value) => WithSlash(s => s.SetFlag(0xA1, value));
        partial void OnSlashLowBatteryAlertChanged(bool value) => WithSlash(s => s.SetFlag(0xA2, value));
        partial void OnSlashBatteryIndicatorChanged(bool value) => WithSlash(s => { s.SetBatteryAnimation(value); s.SetFlag(0xA5, value); });
        partial void OnSlashPowerSavingChanged(bool value) => WithSlash(s => { s.SetPowerSaving(value, SlashDimLevel); s.SetFlag(0xA8, value); });

        partial void OnSlashSleepPatternChanged(int value)
        {
            WithSlash(s => s.SetRecordByte(0xA1, 6, value == 0 ? (byte)0x00 : Arsenal.AnimeMatrix.SlashDevice.GetModeCode((Arsenal.AnimeMatrix.SlashMode)MatrixMode)));
        }

        partial void OnSlashDimLevelChanged(int value)
        {
            if (!_isReady) return;
            AppConfig.Set("slash_dim", value);
            WithSlash(s => s.SetPowerSaving(SlashPowerSaving, value));
        }

        private void WithSlash(Action<Arsenal.AnimeMatrix.SlashDevice> action)
        {
            if (!_isReady || Program.matrixControl?.deviceSlash is not { } slash) return;
            try { action(slash); } catch { }
        }

        private static void UpdateSelection(IEnumerable<SelectableIntOption> options, int selected)
        {
            foreach (var option in options) option.IsSelected = option.Value == selected;
        }

        private static int ToInt(object? param, int defaultValue = 0)
        {
            if (param == null) return defaultValue;
            if (param is int i) return i;
            if (int.TryParse(param.ToString(), out int parsed)) return parsed;
            return defaultValue;
        }
    }
}
