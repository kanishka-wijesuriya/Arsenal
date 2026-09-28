using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;

namespace Arsenal.UI.ViewModels
{
    public partial class PerformanceViewModel : ObservableObject
    {
        private readonly IPerformanceService _performanceService;
        private readonly ICoolingService _coolingService;

        [ObservableProperty]
        private int _currentMode = 1;

        [ObservableProperty]
        private PerformancePlanItem? _selectedProfile;

        [ObservableProperty]
        private bool _isProfileManagerOpen;

        [ObservableProperty]
        private bool _isCreateComposerOpen;

        [ObservableProperty]
        private string _newProfileName = string.Empty;

        [ObservableProperty]
        private string _profileManagerMessage = string.Empty;

        private bool _isRefreshingProfiles;

        public ObservableCollection<PerformancePlanItem> Profiles { get; } = new();
        public ObservableCollection<PerformancePlanItem> VisibleProfiles { get; } = new();
        public bool HasMoreProfiles => Profiles.Count > 5;
        public bool CanCreateProfile => Profiles.Count < Arsenal.Mode.Modes.MaxModes;

        [ObservableProperty]
        private int _spl = 45;

        [ObservableProperty]
        private int _sppt = 65;

        [ObservableProperty]
        private int _fppt = 80;

        [ObservableProperty]
        private int _cpuTempLimit = 95;

        [ObservableProperty]
        private int _cpuUndervolt = 0;

        [ObservableProperty]
        private int _igpuUndervolt = 0;

        [ObservableProperty]
        private bool _applyUndervolt = false;

        [ObservableProperty]
        private int _gpuCoreOffset = 0;

        [ObservableProperty]
        private int _gpuMemoryOffset = 0;

        [ObservableProperty]
        private int _gpuBoost = 25;

        [ObservableProperty]
        private int _gpuTempTarget = 87;

        [ObservableProperty]
        private int _gpuPowerTarget = 140;

        [ObservableProperty]
        private int _gpuClockLimit = 0;

        /// <summary>
        /// The top of the track is the writer's "no cap" rather than a 3000 MHz ceiling,
        /// so it reads as Default - taken from the range so a card with a different
        /// ceiling still labels its own top step.
        /// </summary>
        public string GpuClockLimitText => GpuClockLimit >= GpuClockLimitRange.Maximum
            ? "Default"
            : $"{GpuClockLimit} MHz";

        /// <summary>
        /// The TGP as a wattage rather than as the offset the firmware register takes.
        /// The stored value is the offset - that is what the writer sends - but an
        /// offset is not a number anyone recognises on a GPU, and a track labelled
        /// 0-55 W under a card running at 125 W is the same mismatch this whole pass is
        /// about. The range is shifted to match, so track, scale and readout agree.
        /// </summary>
        public int GpuPowerTotal
        {
            get => GpuPowerBaseWatts + GpuPowerTarget;
            set => GpuPowerTarget = value - GpuPowerBaseWatts;
        }

        public ControlRange GpuPowerTotalRange => new(
            GpuPowerBaseWatts + GpuPowerOffsetRange.Minimum,
            GpuPowerBaseWatts + GpuPowerOffsetRange.Maximum);

        [ObservableProperty]
        private int _fanHysteresisUp = 0;

        [ObservableProperty]
        private int _fanHysteresisDown = 0;

        [ObservableProperty]
        private bool _applyFans = true;

        [ObservableProperty]
        private bool _applyPower = true;

        [ObservableProperty]
        private int _cpuBoost = 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedFanCurve))]
        private int _selectedFanTab = 0; // 0=CPU, 1=GPU, 2=Mid

        /// <summary>The curve the editor is showing, chosen by the segmented control.</summary>
        public FanCurveModel? SelectedFanCurve => SelectedFanTab switch
        {
            1 => GpuFanCurve,
            2 => MidFanCurve,
            _ => CpuFanCurve
        };

        /// <summary>
        /// Whether this chassis has a third fan. Models without one still answer the
        /// curve read, so the choice has to come from the model list rather than from
        /// whether a curve came back.
        /// </summary>
        public bool HasMidFan { get; } = AppConfig.Is("mid_fan");

        [RelayCommand]
        public void SelectFanTab(object? tabParam)
        {
            SelectedFanTab = tabParam is int value ? value
                : int.TryParse(tabParam?.ToString(), out int parsed) ? parsed : 0;
        }

        [ObservableProperty]
        private FanCurveModel _cpuFanCurve;

        [ObservableProperty]
        private FanCurveModel _gpuFanCurve;

        [ObservableProperty]
        private FanCurveModel _midFanCurve;

        [ObservableProperty]
        private string _calibrationStatus = "";

        [ObservableProperty]
        private bool _isCalibrating = false;

        /// <summary>Undervolt offsets this CPU will actually accept.</summary>
        public bool IsUndervoltSupported { get; }

        /// <summary>The integrated-GPU offset, supported on fewer parts still.</summary>
        public bool IsIgpuUndervoltSupported { get; }

        /// <summary>The row goes away entirely when neither offset applies.</summary>
        public bool IsUndervoltRowVisible => IsUndervoltSupported || IsIgpuUndervoltSupported;

        /// <summary>Custom fan curves need the ACPI performance-mode interface.</summary>
        public bool IsCustomFansSupported { get; }

        /// <summary>Clock and power targets are meaningless without a discrete GPU.</summary>
        public bool IsDedicatedGpuPresent { get; }

        /// <summary>
        /// What this machine will actually accept, for the sliders to draw against.
        /// Captured once: none of them change while the application is running, and a
        /// binding that re-read them would re-run the nvidia-smi probe behind
        /// <see cref="IGpuService.GpuPowerOffsetRange"/>.
        /// </summary>
        public ControlRange PowerLimitRange { get; }
        public ControlRange CpuTempRange { get; }
        public ControlRange CpuUndervoltRange { get; }
        public ControlRange IgpuUndervoltRange { get; }
        public ControlRange FanHysteresisRange { get; }
        public ControlRange GpuCoreOffsetRange { get; }
        public ControlRange GpuMemoryOffsetRange { get; }
        public ControlRange GpuClockLimitRange { get; }
        public ControlRange GpuBoostRange { get; }
        public ControlRange GpuTempRange { get; }
        public ControlRange GpuPowerOffsetRange { get; }

        /// <summary>The TGP the offset sits on top of, so the readout can show a total.</summary>
        public int GpuPowerBaseWatts { get; }

        /// <summary>Hides the TGP row on machines whose firmware has no base to offset.</summary>
        public bool IsGpuPowerAdjustable { get; }

        public PerformanceViewModel(IPerformanceService performanceService, ICoolingService coolingService, IGpuService gpuService)
        {
            _performanceService = performanceService;
            _coolingService = coolingService;

            IsUndervoltSupported = _performanceService.IsUndervoltSupported;
            IsIgpuUndervoltSupported = _performanceService.IsIgpuUndervoltSupported;
            IsCustomFansSupported = _coolingService.CustomFansSupported;
            IsDedicatedGpuPresent = gpuService.HasDedicatedGpu;

            PowerLimitRange = _performanceService.PowerLimitRange;
            CpuTempRange = _performanceService.CpuTempRange;
            CpuUndervoltRange = _performanceService.CpuUndervoltRange;
            IgpuUndervoltRange = _performanceService.IgpuUndervoltRange;
            FanHysteresisRange = _performanceService.FanHysteresisRange;

            GpuCoreOffsetRange = gpuService.GpuCoreOffsetRange;
            GpuMemoryOffsetRange = gpuService.GpuMemoryOffsetRange;
            GpuClockLimitRange = gpuService.GpuClockLimitRange;
            GpuBoostRange = gpuService.GpuBoostRange;
            GpuTempRange = gpuService.GpuTempRange;

            // Alone among these, the TGP range costs an nvidia-smi call to answer, so
            // it is asked for only where a row will draw it.
            IsGpuPowerAdjustable = IsDedicatedGpuPresent && gpuService.IsGpuPowerAdjustable;
            GpuPowerOffsetRange = IsGpuPowerAdjustable ? gpuService.GpuPowerOffsetRange : default;
            GpuPowerBaseWatts = IsGpuPowerAdjustable ? gpuService.GpuPowerBaseWatts : 0;

            LoadCurrentProfile();

            _coolingService.CalibrationStatusChanged += (s) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    CalibrationStatus = s;
                    IsCalibrating = true;
                });
            };

            _coolingService.CalibrationCompleted += () =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    IsCalibrating = false;
                });
            };

            _performanceService.ModeChanged += _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(LoadCurrentProfile);
            _performanceService.ProfilesChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshProfiles);
        }

        public void LoadCurrentProfile()
        {
            RefreshProfiles();
            var profile = _performanceService.GetCurrentProfile();
            CurrentMode = profile.ModeIndex;

            // Clamped on the way in, not left to the slider to coerce on the way out.
            // A stored value above the track - a 150 W limit under a 140 W ceiling, a
            // 25 W boost on a part that tops out at 20 - is one the writer was already
            // dropping, so showing it unchanged claims a setting that is not in force.
            // Bringing it onto the track makes the readout, the track and the next
            // write agree, and the defaults are clamped too because they were picked
            // for a mid-range chassis rather than for this one.
            Spl = PowerLimitRange.Clamp(profile.Spl > 0 ? profile.Spl : 45);
            Sppt = PowerLimitRange.Clamp(profile.Sppt > 0 ? profile.Sppt : 65);
            Fppt = PowerLimitRange.Clamp(profile.Fppt > 0 ? profile.Fppt : 80);
            CpuTempLimit = CpuTempRange.Clamp(profile.CpuTempLimit > 0 ? profile.CpuTempLimit : CpuTempRange.Maximum);
            CpuUndervolt = CpuUndervoltRange.Clamp(profile.CpuUndervolt);
            IgpuUndervolt = IgpuUndervoltRange.Clamp(profile.IgpuUndervolt);
            ApplyUndervolt = profile.ApplyUndervolt;
            GpuCoreOffset = GpuCoreOffsetRange.Clamp(profile.GpuCoreOffset);
            GpuMemoryOffset = GpuMemoryOffsetRange.Clamp(profile.GpuMemoryOffset);
            GpuBoost = GpuBoostRange.Clamp(profile.GpuBoost >= 0 ? profile.GpuBoost : GpuBoostRange.Maximum);
            GpuTempTarget = GpuTempRange.Clamp(profile.GpuTempTarget > 0 ? profile.GpuTempTarget : GpuTempRange.Maximum);

            // Left alone where there is no base TGP to offset: with no row to draw it,
            // clamping to a collapsed range would only rewrite a stored value nothing
            // is reading.
            GpuPowerTarget = IsGpuPowerAdjustable
                ? GpuPowerOffsetRange.Clamp(profile.GpuPowerTarget >= 0 ? profile.GpuPowerTarget : GpuPowerOffsetRange.Maximum)
                : profile.GpuPowerTarget;

            // Below the minimum is the writer's "no cap", not a value to be raised into
            // range - the top of the track means the same thing and reads as "Default".
            GpuClockLimit = profile.GpuClockLimit < GpuClockLimitRange.Minimum
                ? GpuClockLimitRange.Maximum
                : GpuClockLimitRange.Clamp(profile.GpuClockLimit);
            FanHysteresisUp = FanHysteresisRange.Clamp(profile.FanHysteresisUp);
            FanHysteresisDown = FanHysteresisRange.Clamp(profile.FanHysteresisDown);
            ApplyFans = profile.ApplyFans;
            ApplyPower = profile.ApplyPower;
            CpuBoost = profile.CpuBoost >= 0 ? profile.CpuBoost : 0;

            CpuFanCurve = _coolingService.GetFanCurve(0, CurrentMode);
            GpuFanCurve = _coolingService.GetFanCurve(1, CurrentMode);
            MidFanCurve = _coolingService.GetFanCurve(2, CurrentMode);

            // The editor shows one of the three, so it has to be told they were replaced.
            OnPropertyChanged(nameof(SelectedFanCurve));
        }

        private void RefreshProfiles()
        {
            _isRefreshingProfiles = true;
            try
            {
                Profiles.Clear();
                foreach (PerformancePlanInfo plan in _performanceService.GetProfiles())
                    Profiles.Add(new PerformancePlanItem(plan));

                SelectedProfile = Profiles.FirstOrDefault(plan => plan.ModeIndex == _performanceService.CurrentMode)
                    ?? Profiles.FirstOrDefault();
                foreach (PerformancePlanItem plan in Profiles)
                    plan.IsSelected = plan.ModeIndex == _performanceService.CurrentMode;

                VisibleProfiles.Clear();
                List<PerformancePlanItem> visible = Profiles.Take(5).ToList();
                if (SelectedProfile is not null && !visible.Contains(SelectedProfile) && visible.Count == 5)
                {
                    visible.RemoveAt(visible.Count - 1);
                    visible.Add(SelectedProfile);
                }
                foreach (PerformancePlanItem plan in visible) VisibleProfiles.Add(plan);

                OnPropertyChanged(nameof(HasMoreProfiles));
                OnPropertyChanged(nameof(CanCreateProfile));
            }
            finally
            {
                _isRefreshingProfiles = false;
            }
        }

        partial void OnSelectedProfileChanged(PerformancePlanItem? value)
        {
            if (_isRefreshingProfiles || value is null || value.ModeIndex == _performanceService.CurrentMode) return;
            SelectProfileMode(value.ModeIndex);
        }

        partial void OnGpuClockLimitChanged(int value) => OnPropertyChanged(nameof(GpuClockLimitText));

        partial void OnGpuPowerTargetChanged(int value) => OnPropertyChanged(nameof(GpuPowerTotal));

        [RelayCommand]
        public void SelectProfileMode(object? modeParam)
        {
            int mode = modeParam is int value ? value : int.TryParse(modeParam?.ToString(), out int parsed) ? parsed : 1;
            _performanceService.SetMode(mode);
            LoadCurrentProfile();
        }

        [RelayCommand]
        public void OpenCreateProfile()
        {
            ProfileManagerMessage = string.Empty;
            NewProfileName = NextProfileName();
            IsCreateComposerOpen = true;
            IsProfileManagerOpen = true;
        }

        [RelayCommand]
        public void OpenProfileManager()
        {
            ProfileManagerMessage = string.Empty;
            IsCreateComposerOpen = false;
            IsProfileManagerOpen = true;
        }

        [RelayCommand]
        public void CloseProfileManager()
        {
            foreach (PerformancePlanItem plan in Profiles) plan.IsDeletePending = false;
            IsCreateComposerOpen = false;
            IsProfileManagerOpen = false;
        }

        [RelayCommand]
        public void ShowCreateComposer()
        {
            if (!CanCreateProfile) return;
            ProfileManagerMessage = string.Empty;
            NewProfileName = NextProfileName();
            IsCreateComposerOpen = true;
        }

        [RelayCommand]
        public void CreateProfile()
        {
            if (!CanCreateProfile) return;
            string name = NewProfileName.Trim();
            int mode = _performanceService.CreateProfile(string.IsNullOrWhiteSpace(name) ? null : name);
            if (mode < 0)
            {
                ProfileManagerMessage = "The plan library is full. Remove a custom plan before creating another.";
                return;
            }

            IsCreateComposerOpen = false;
            ProfileManagerMessage = "Custom plan created and selected.";
            LoadCurrentProfile();
        }

        [RelayCommand]
        public void SelectManagedProfile(object? planParam)
        {
            if (planParam is not PerformancePlanItem plan) return;
            SelectProfileMode(plan.ModeIndex);
            CloseProfileManager();
        }

        [RelayCommand]
        public void RenameProfile(object? planParam)
        {
            if (planParam is not PerformancePlanItem plan || !plan.IsCustom) return;
            if (!_performanceService.RenameProfile(plan.ModeIndex, plan.Name))
                RefreshProfiles();
            else
                ProfileManagerMessage = "Plan name saved.";
        }

        [RelayCommand]
        public void RequestDeleteProfile(object? planParam)
        {
            if (planParam is not PerformancePlanItem plan || !plan.IsCustom) return;
            foreach (PerformancePlanItem item in Profiles) item.IsDeletePending = ReferenceEquals(item, plan);
        }

        [RelayCommand]
        public void CancelDeleteProfile()
        {
            foreach (PerformancePlanItem item in Profiles) item.IsDeletePending = false;
        }

        [RelayCommand]
        public void ConfirmDeleteProfile(object? planParam)
        {
            if (planParam is not PerformancePlanItem plan || !plan.IsCustom) return;
            _performanceService.DeleteProfile(plan.ModeIndex);
            ProfileManagerMessage = "Custom plan deleted.";
            RefreshProfiles();
            LoadCurrentProfile();
        }

        private string NextProfileName()
        {
            HashSet<string> names = Profiles.Select(plan => plan.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int number = 1; number <= Arsenal.Mode.Modes.MaxModes; number++)
            {
                string candidate = $"{"Custom Plan"} {number}";
                if (!names.Contains(candidate)) return candidate;
            }
            return "Custom Plan";
        }

        [RelayCommand]
        public void ApplySettings()
        {
            var profile = new PerformanceProfile
            {
                ModeIndex = CurrentMode,
                Spl = Spl,
                Sppt = Sppt,
                Fppt = Fppt,
                CpuTempLimit = CpuTempLimit,
                CpuUndervolt = CpuUndervolt,
                IgpuUndervolt = IgpuUndervolt,
                ApplyUndervolt = ApplyUndervolt,
                GpuCoreOffset = GpuCoreOffset,
                GpuMemoryOffset = GpuMemoryOffset,
                GpuBoost = GpuBoost,
                GpuTempTarget = GpuTempTarget,
                GpuPowerTarget = GpuPowerTarget,
                GpuClockLimit = GpuClockLimit,
                FanHysteresisUp = FanHysteresisUp,
                FanHysteresisDown = FanHysteresisDown,
                ApplyFans = ApplyFans,
                ApplyPower = ApplyPower,
                CpuBoost = CpuBoost
            };

            _performanceService.SaveProfile(profile);
            _coolingService.SaveFanCurve(0, CurrentMode, CpuFanCurve);
            _coolingService.SaveFanCurve(1, CurrentMode, GpuFanCurve);
            _coolingService.SaveFanCurve(2, CurrentMode, MidFanCurve);
            _coolingService.ApplyFanCurves(CurrentMode);
        }

        [RelayCommand]
        public void ResetSettings()
        {
            _performanceService.ResetProfile(CurrentMode);
            _coolingService.ResetFanCurves(CurrentMode);
            LoadCurrentProfile();
        }

        [RelayCommand]
        public void StartFanCalibration()
        {
            _coolingService.StartCalibration();
        }
    }
}
