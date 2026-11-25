using Arsenal.Application.Models;
using Arsenal.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arsenal.UI.ViewModels
{
    /// <summary>
    /// What the laptop's own extra keys do: G-Helper's key bindings, M1 to M5 and the
    /// Fn combinations, with the labels and keys each model actually has.
    /// </summary>
    public partial class HotkeyBindingsViewModel : ObservableObject
    {
        public List<HotkeyBindingViewModel> Keys { get; } = new();

        public bool HasKeys => Keys.Count > 0;

        public HotkeyBindingsViewModel()
        {
            if (AppConfig.IsAlly())
            {
                Add("cc", "Command Center");
                Add("m4", "ROG");
                Add("paddle", "Back paddles");
                return;
            }

            bool arcnm = AppConfig.IsARCNM();
            bool noMKeys = AppConfig.NoMKeys();
            bool zenPro = AppConfig.IsVivoZenPro();

            if (!arcnm && !zenPro) Add("m1", noMKeys ? "Fn+F2" : "M1");
            if (!arcnm && !zenPro) Add("m2", noMKeys ? "Fn+F3" : "M2");
            if (!zenPro) Add("m3", arcnm ? "Fn+F6" : noMKeys ? "Fn+F4" : "M3");
            if (!arcnm && (!noMKeys || AppConfig.IsM4Button()))
                Add("m4", zenPro ? "Fn+F12" : AppConfig.IsStrix() ? "M5 / ROG" : "M4 / ROG");
            if (AppConfig.IsStrix()) Add("m5", "M4");
            if (!arcnm && !noMKeys && !zenPro && !AppConfig.MediaKeys()) Add("fnf4", "Fn+F4");
            if (!AppConfig.IsProArt()) Add("fnc", "Fn+C");
            if (AppConfig.IsProArt() || !AppConfig.IsNoFNV()) Add("fnv", AppConfig.IsProArt() ? "Fn+C" : "Fn+V");
            if (AppConfig.IsTUF() && !AppConfig.ContainsModel("FA401W")) Add("fne", "Fn+Num Enter");
        }

        private void Add(string name, string label) => Keys.Add(new HotkeyBindingViewModel(name, label));
    }

    /// <summary>One key and the action bound to it.</summary>
    public partial class HotkeyBindingViewModel : ObservableObject
    {
        private const string Unset = "--------------";

        public string Name { get; }
        public string Label { get; }
        public List<PeripheralOptionModel> Options { get; } = new();

        /// <summary>Index into <see cref="Options"/>; the action itself is its key below.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustom))]
        private int _selected;

        [ObservableProperty]
        private string _command;

        /// <summary>A custom action runs the command typed beside it.</summary>
        public bool IsCustom => _actions.ElementAtOrDefault(Selected) == "custom";

        private readonly List<string> _actions = new();
        private readonly bool _loading;

        public HotkeyBindingViewModel(string name, string label)
        {
            Name = name;
            Label = label;

            var actions = new List<(string Key, string Label)>
            {
                ("", Unset),
                ("volume_down", "Volume down"),
                ("volume_up", "Volume up"),
                ("backlight_down", "Keyboard backlight down"),
                ("backlight_up", "Keyboard backlight up"),
                ("mute", "Mute"),
                ("screenshot", "Print screen"),
                ("play", "Play / pause"),
                ("aura", "Next Aura mode"),
                ("performance", "Next performance mode"),
                ("screen", "Turn off screen"),
                ("lock", "Lock screen"),
                ("miniled", "Toggle Mini-LED"),
                ("fnlock", "Toggle Fn lock"),
                ("brightness_down", "Display brightness down"),
                ("brightness_up", "Display brightness up"),
                ("visual", "Next colour profile"),
                ("touchpad", "Toggle touchpad"),
                ("touchscreen", "Toggle touchscreen"),
                ("micmute", "Mute microphone"),
                ("ghelper", "Open Arsenal"),
                ("overlay", "Hardware overlay"),
                ("custom", "Custom command"),
            };

            if (AppConfig.IsDUO())
            {
                actions.Add(("screenpad_down", "ScreenPad brightness down"));
                actions.Add(("screenpad_up", "ScreenPad brightness up"));
            }

            if (AppConfig.IsAlly()) actions.Add(("controller", "Controller mode"));

            // An unset key does what the key does by default, which the list names
            // in place of the blank, and that action is not offered a second time.
            (string Default, string? Duplicate) = name switch
            {
                "m1" => ("Volume down", "volume_down"),
                "m2" => ("Volume up", "volume_up"),
                "m3" => ("Mute microphone", "micmute"),
                "m4" => ("Open Arsenal", "ghelper"),
                "m5" => ("Next performance mode", "performance"),
                "fnf4" => ("Next Aura mode", "aura"),
                "fnc" => ("Toggle Fn lock", "fnlock"),
                "fnv" => ("Next colour profile", "visual"),
                "fne" => ("Calculator", (string?)null),
                _ => (Unset, null),
            };
            actions[0] = ("", Default);
            if (Duplicate is not null) actions.RemoveAll(action => action.Key == Duplicate);

            for (int i = 0; i < actions.Count; i++)
            {
                _actions.Add(actions[i].Key);
                Options.Add(new PeripheralOptionModel { Value = i, Label = actions[i].Label });
            }

            _loading = true;
            Selected = Math.Max(0, _actions.IndexOf(AppConfig.GetString(name) ?? ""));
            _command = AppConfig.GetString(name + "_custom") ?? "";
            _loading = false;
        }

        partial void OnSelectedChanged(int value)
        {
            if (_loading || value < 0 || value >= _actions.Count) return;

            AppConfig.Set(Name, _actions[value]);
            if (Name is "m1" or "m2" or "m3" or "m4" or "m5") MKeyControl.ApplyAll();
            Program.inputDispatcher?.RegisterKeys();
        }

        partial void OnCommandChanged(string value)
        {
            if (!_loading) AppConfig.Set(Name + "_custom", value);
        }
    }
}
