using Arsenal.Ally;
using Arsenal.Application.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;

namespace Arsenal.UI.ViewModels
{
    /// <summary>A key, button or action the Ally's controls can be bound to.</summary>
    public sealed record AllyBindingOption(string Code, string Name, string Group);

    /// <summary>
    /// The ROG Ally's built-in controller: G-Helper's controller window.
    /// </summary>
    /// <remarks>
    /// Bindings, turbo and deadzones live in the config and are sent to the controller
    /// by <see cref="AllyControl"/>, which also sends them again whenever the controller
    /// mode changes. Each change here is written at once.
    /// </remarks>
    public partial class HandheldControllerViewModel : ObservableObject
    {
        private bool _loading;
        private int _deadzoneGeneration;

        /// <summary>Each physical control, by the suffix its bindings are stored under.</summary>
        private static readonly (string Key, string Label)[] Controls =
        {
            ("m1", "M1"), ("m2", "M2"),
            ("a", "A"), ("b", "B"), ("x", "X"), ("y", "Y"),
            ("du", "D-pad up"), ("dd", "D-pad down"), ("dl", "D-pad left"), ("dr", "D-pad right"),
            ("lt", "Left trigger"), ("rt", "Right trigger"), ("lb", "Left bumper"), ("rb", "Right bumper"),
            ("ls", "Left stick click"), ("rs", "Right stick click"),
            ("vb", "View"), ("mb", "Menu"),
        };

        public IReadOnlyList<PeripheralOptionModel> ControlOptions { get; } =
            Controls.Select((control, index) => new PeripheralOptionModel { Value = index, Label = control.Label }).ToList();

        public ListCollectionView BindingOptions { get; }

        public IReadOnlyList<PeripheralOptionModel> TurboOptions { get; } =
            new[] { 0, 50, 100, 150, 200, 250, 300, 400, 500 }
                .Select(ms => new PeripheralOptionModel { Value = ms, Label = ms == 0 ? "Off" : $"{ms} ms" })
                .ToList();

        /// <summary>The Xbox edition, which also has an enhanced vibration mode.</summary>
        public bool IsXboxAlly { get; } = AppConfig.IsXboxAlly();

        [ObservableProperty] private int _selectedControl;
        [ObservableProperty] private string _primary = "";
        [ObservableProperty] private string _secondary = "";
        [ObservableProperty] private int _turboPrimary;
        [ObservableProperty] private int _turboSecondary;

        [ObservableProperty] private bool _controllerDisabled;
        [ObservableProperty] private bool _enhancedVibration;
        [ObservableProperty] private int _vibration = 100;

        [ObservableProperty] private int _leftStickMin;
        [ObservableProperty] private int _leftStickMax = 100;
        [ObservableProperty] private int _rightStickMin;
        [ObservableProperty] private int _rightStickMax = 100;
        [ObservableProperty] private int _leftTriggerMin;
        [ObservableProperty] private int _leftTriggerMax = 100;
        [ObservableProperty] private int _rightTriggerMin;
        [ObservableProperty] private int _rightTriggerMax = 100;

        public HandheldControllerViewModel()
        {
            var options = new List<AllyBindingOption>();
            foreach (var (group, items) in AllyControl.BindingGroups)
            {
                foreach (var (code, name) in items)
                {
                    if (group == "")
                        options.Add(new AllyBindingOption(code, code == "" ? "Default" : "Disabled", "Button"));
                    else
                        options.Add(new AllyBindingOption(code, name, group));
                }
            }

            BindingOptions = new ListCollectionView(options);
            BindingOptions.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AllyBindingOption.Group)));

            Load();
        }

        private string ControlKey => Controls[Math.Clamp(SelectedControl, 0, Controls.Length - 1)].Key;

        private void Load()
        {
            _loading = true;
            try
            {
                LoadBinding();
                ControllerDisabled = AppConfig.Is("controller_disabled");
                EnhancedVibration = AppConfig.Is("vibra_enhanced");
                Vibration = AppConfig.Get("vibra", 100);
                LeftStickMin = AppConfig.Get("ls_min", 0);
                LeftStickMax = AppConfig.Get("ls_max", 100);
                RightStickMin = AppConfig.Get("rs_min", 0);
                RightStickMax = AppConfig.Get("rs_max", 100);
                LeftTriggerMin = AppConfig.Get("lt_min", 0);
                LeftTriggerMax = AppConfig.Get("lt_max", 100);
                RightTriggerMin = AppConfig.Get("rt_min", 0);
                RightTriggerMax = AppConfig.Get("rt_max", 100);
            }
            finally
            {
                _loading = false;
            }
        }

        private void LoadBinding()
        {
            Primary = AppConfig.GetString("bind_" + ControlKey, "") ?? "";
            Secondary = AppConfig.GetString("bind2_" + ControlKey, "") ?? "";
            TurboPrimary = AppConfig.Get("turbo_" + ControlKey, 0);
            TurboSecondary = AppConfig.Get("turbo2_" + ControlKey, 0);
        }

        partial void OnSelectedControlChanged(int value)
        {
            bool wasLoading = _loading;
            _loading = true;
            try { LoadBinding(); }
            finally { _loading = wasLoading; }
        }

        partial void OnPrimaryChanged(string value) => SetBinding("bind_", value);
        partial void OnSecondaryChanged(string value) => SetBinding("bind2_", value);

        private void SetBinding(string prefix, string? code)
        {
            if (_loading) return;
            if (!string.IsNullOrEmpty(code)) AppConfig.Set(prefix + ControlKey, code);
            else AppConfig.Remove(prefix + ControlKey);
            AllyControl.ApplyMode();
        }

        partial void OnTurboPrimaryChanged(int value) => SetTurbo("turbo_", value);
        partial void OnTurboSecondaryChanged(int value) => SetTurbo("turbo2_", value);

        private void SetTurbo(string prefix, int ms)
        {
            if (_loading) return;
            if (ms > 0) AppConfig.Set(prefix + ControlKey, ms);
            else AppConfig.Remove(prefix + ControlKey);
            AllyControl.ApplyMode();
        }

        partial void OnControllerDisabledChanged(bool value)
        {
            if (_loading) return;
            AppConfig.Set("controller_disabled", value ? 1 : 0);
            AllyControl.DisableXBoxController(value);
        }

        partial void OnEnhancedVibrationChanged(bool value)
        {
            if (_loading) return;
            AppConfig.Set("vibra_enhanced", value ? 1 : 0);
            AllyControl.SetVibrationMode();
        }

        partial void OnVibrationChanged(int value) => SetDeadzone("vibra", value);
        partial void OnLeftStickMinChanged(int value) => SetDeadzone("ls_min", value);
        partial void OnLeftStickMaxChanged(int value) => SetDeadzone("ls_max", value);
        partial void OnRightStickMinChanged(int value) => SetDeadzone("rs_min", value);
        partial void OnRightStickMaxChanged(int value) => SetDeadzone("rs_max", value);
        partial void OnLeftTriggerMinChanged(int value) => SetDeadzone("lt_min", value);
        partial void OnLeftTriggerMaxChanged(int value) => SetDeadzone("lt_max", value);
        partial void OnRightTriggerMinChanged(int value) => SetDeadzone("rt_min", value);
        partial void OnRightTriggerMaxChanged(int value) => SetDeadzone("rt_max", value);

        /// <summary>
        /// Stores the value at once and sends the whole set once the slider has been
        /// still for a moment, as G-Helper sends it when the track is released.
        /// </summary>
        private void SetDeadzone(string key, int value)
        {
            if (_loading) return;
            AppConfig.Set(key, value);

            int generation = ++_deadzoneGeneration;
            _ = Task.Delay(250).ContinueWith(_ =>
            {
                if (generation == _deadzoneGeneration) AllyControl.SetDeadzones();
            }, TaskScheduler.Default);
        }

        /// <summary>Puts the deadzones and vibration back to their defaults. Bindings are kept.</summary>
        [RelayCommand]
        private void Reset()
        {
            foreach (string key in new[] { "ls_min", "ls_max", "rs_min", "rs_max", "lt_min", "lt_max", "rt_min", "rt_max", "vibra", "vibra_enhanced" })
                AppConfig.Remove(key);

            Load();
            Task.Run(() =>
            {
                AllyControl.SetDeadzones();
                if (IsXboxAlly) AllyControl.SetVibrationMode();
            });
        }
    }
}
