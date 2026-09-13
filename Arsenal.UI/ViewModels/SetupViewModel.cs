using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Helpers;
using Arsenal.UI.Services.Remote;
using QRCoder;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Arsenal.UI.ViewModels
{
    /// <summary>Panels of the first-run wizard, in the order they are shown.</summary>
    public enum SetupStage
    {
        Welcome,
        Startup,
        Experience,
        AsusSoftware,
        Phone,
        Finish
    }

    /// <summary>One entry in the wizard's step rail.</summary>
    public sealed partial class SetupStep : ObservableObject
    {
        public SetupStep(SetupStage stage, int index, string title, string caption, string railLabel,
                         Wpf.Ui.Controls.SymbolRegular icon)
        {
            Stage = stage;
            Index = index;
            Title = title;
            Caption = caption;
            RailLabel = railLabel;
            Icon = icon;
        }

        public SetupStage Stage { get; }
        public int Index { get; }

        /// <summary>Number shown in the rail, 1-based.</summary>
        public int Number => Index + 1;

        /// <summary>Heading above the panel.</summary>
        public string Title { get; }

        /// <summary>One line under the heading.</summary>
        public string Caption { get; }

        /// <summary>Short form used in the rail, where there is no room for the title.</summary>
        public string RailLabel { get; }

        public Wpf.Ui.Controls.SymbolRegular Icon { get; }

        /// <summary>The rail runs a line down to the next step; the last one has none.</summary>
        public bool ShowConnector => Stage != SetupStage.Finish;

        /// <summary>
        /// Finish is the outcome rather than a question, so its rail node carries a tick
        /// instead of a number - which also keeps the numbering agreeing with the
        /// "step N of 5" counter above the rail.
        /// </summary>
        public bool ShowNumber => Stage != SetupStage.Finish;

        [ObservableProperty]
        private bool _isActive;

        /// <summary>Steps the user has already moved past; they show a tick in the rail.</summary>
        [ObservableProperty]
        private bool _isDone;
    }

    public enum SetupTaskState { Pending, Running, Done, Skipped, Failed }

    /// <summary>
    /// A line on the completion checklist. The wizard fills these in as it applies the
    /// choices, so the last screen shows what actually happened rather than a bare
    /// "done" - including the steps that were deliberately left alone.
    /// </summary>
    public sealed partial class SetupTask : ObservableObject
    {
        public SetupTask(string label) => Label = label;

        public string Label { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPending))]
        [NotifyPropertyChangedFor(nameof(IsRunning))]
        [NotifyPropertyChangedFor(nameof(IsDone))]
        [NotifyPropertyChangedFor(nameof(IsSkipped))]
        [NotifyPropertyChangedFor(nameof(IsFailed))]
        private SetupTaskState _state = SetupTaskState.Pending;

        [ObservableProperty]
        private string _detail = string.Empty;

        public bool IsPending => State == SetupTaskState.Pending;
        public bool IsRunning => State == SetupTaskState.Running;
        public bool IsDone => State == SetupTaskState.Done;
        public bool IsSkipped => State == SetupTaskState.Skipped;
        public bool IsFailed => State == SetupTaskState.Failed;
    }

    /// <summary>
    /// First-run setup, as a multi-step wizard.
    ///
    /// Everything here is reachable from Settings, Advanced and Mobile Companion too;
    /// the point of gathering it is that the choices which matter most are the ones a
    /// new user is least likely to go looking for - above all whether the app should
    /// run normally or request administrator rights for machine-wide controls.
    ///
    /// The phone step is here for the same reason: pairing needs a code that is only
    /// visible on this screen, and nobody goes looking for a pairing screen they do not
    /// know exists.
    /// </summary>
    public partial class SetupViewModel : ObservableObject, IDisposable
    {
        public bool CanSelfUpdate => App.CanSelfUpdate;

        /// <summary>Bumped when the steps change materially, so setup runs again.</summary>
        public const int CurrentSetupVersion = 3;

        public static bool IsSetupNeeded => AppConfig.Get("setup_version", 0) < CurrentSetupVersion;

        private readonly RemoteCompanionService _companion;

        /// <summary>Polls for a phone that has just paired. Runs only on the phone step.</summary>
        private readonly DispatcherTimer _pairingTimer;

        /// <summary>What each checklist line actually does, kept beside the label.</summary>
        private readonly Dictionary<SetupTask, Func<bool>> _work = new();

        private string _renderedPairingCode = string.Empty;

        /// <summary>
        /// How many phones were already paired when the wizard opened. Anything above
        /// this count was paired here, which is what the step reports on. Re-taken on
        /// <see cref="Restart"/>, so a second run does not claim last run's phone.
        /// </summary>
        private int _devicesAtStart;

        // ===== Steps =====

        public ObservableCollection<SetupStep> Steps { get; } = new()
        {
            new SetupStep(SetupStage.Welcome, 0, "Welcome to Arsenal",
                "A lighter way to drive your ASUS laptop. Four short steps and you are done.",
                "Welcome", Wpf.Ui.Controls.SymbolRegular.Sparkle24),
            new SetupStep(SetupStage.Startup, 1, "Start with Windows",
                "Choose normal access or administrator rights, then decide whether Arsenal starts at sign-in.",
                "Startup", Wpf.Ui.Controls.SymbolRegular.Power24),
            new SetupStep(SetupStage.Experience, 2, "Notifications and updates",
                "How much Arsenal says, and whether it looks for new releases.",
                "Notifications", Wpf.Ui.Controls.SymbolRegular.Alert24),
            new SetupStep(SetupStage.AsusSoftware, 3, "ASUS software",
                "Arsenal talks to the same firmware Armoury Crate does, so running both is usually unnecessary.",
                "ASUS software", Wpf.Ui.Controls.SymbolRegular.Wrench24),
            new SetupStep(SetupStage.Phone, 4, "Connect your phone",
                "Optional. Pair the companion app to check temperatures and switch profiles from your phone.",
                "Your phone", Wpf.Ui.Controls.SymbolRegular.PhoneLaptop24),
            new SetupStep(SetupStage.Finish, 5, "All set",
                "Applying your choices.",
                "Finish", Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24)
        };

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CurrentStep))]
        [NotifyPropertyChangedFor(nameof(CurrentStage))]
        [NotifyPropertyChangedFor(nameof(CanGoBack))]
        [NotifyPropertyChangedFor(nameof(IsFinishStep))]
        [NotifyPropertyChangedFor(nameof(IsSkippable))]
        [NotifyPropertyChangedFor(nameof(NextLabel))]
        [NotifyPropertyChangedFor(nameof(Progress))]
        [NotifyPropertyChangedFor(nameof(StepCounter))]
        private int _currentIndex;

        /// <summary>+1 forward, -1 back. Read by the panel presenter to pick a direction.</summary>
        [ObservableProperty]
        private int _direction = 1;

        public SetupStep CurrentStep => Steps[CurrentIndex];
        public SetupStage CurrentStage => CurrentStep.Stage;
        public bool IsFinishStep => CurrentStage == SetupStage.Finish;
        public bool CanGoBack => CurrentIndex > 0 && !IsFinishStep;

        /// <summary>The finish step has nothing left to skip past.</summary>
        public bool IsSkippable => !IsFinishStep;

        public string StepCounter => IsFinishStep ? "Done" : $"Step {CurrentIndex + 1} of {Steps.Count - 1}";

        /// <summary>0 to 1 across the configurable steps, for the footer's progress bar.</summary>
        public double Progress => (double)CurrentIndex / (Steps.Count - 1);

        public string NextLabel => CurrentStage switch
        {
            SetupStage.Welcome => "Get started",
            SetupStage.Phone => "Finish setup",
            SetupStage.Finish => "Done",
            _ => "Continue"
        };

        // ===== Choices =====

        [ObservableProperty]
        private bool _runOnStartup = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StartupDescription))]
        private bool _runAsAdministrator;

        [ObservableProperty]
        private bool _keepChargeLimitAfterReboot = true;

        [ObservableProperty]
        private bool _checkUpdates = true;

        [ObservableProperty]
        private bool _showNotifications = true;

        [ObservableProperty]
        private bool _stopArmouryCrateServices;

        private bool _loadingChoices;

        // ===== Applying =====

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAdvance))]
        private bool _isApplying;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FinishHeadline))]
        [NotifyPropertyChangedFor(nameof(FinishCaption))]
        private bool _isComplete;

        public string FinishHeadline => IsComplete ? "You're all set" : "Setting things up";

        public string FinishCaption => IsComplete
            ? CompletionSummary
            : "This only takes a moment. Registering a scheduled task is the slowest part.";

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public ObservableCollection<SetupTask> Tasks { get; } = new();

        /// <summary>The Next button is only ever blocked while the work is running.</summary>
        public bool CanAdvance => !IsApplying;

        public string ModelName { get; } = AppConfig.GetModel();

        /// <summary>
        /// Whether this particular process has an elevated token. Normal first launch
        /// is intentionally non-admin; enabling the persisted preference relaunches
        /// through the Windows consent prompt.
        /// </summary>
        public bool IsElevated { get; } = ProcessHelper.IsUserAdministrator();

        public bool NeedsElevation => !IsElevated;

        public string StartupDescription => RunAsAdministrator
            ? "Starts elevated at sign-in through Task Scheduler, without a UAC prompt at logon. Manual launches still use the normal Windows consent prompt."
            : "Starts normally at sign-in without administrator rights.";

        // ===== Phone pairing =====

        public string CompanionAddress => _companion.Address;
        public string CompanionPairingCode => _companion.PairingCode;
        public string CompanionFingerprint => _companion.FingerprintDisplay;
        public string CompanionFingerprintBlock => _companion.FingerprintBlock;
        public bool IsCompanionRunning => _companion.IsRunning;
        public bool IsCompanionUnavailable => !_companion.IsRunning;
        public bool IsNetworkAccessAllowed => CompanionFirewall.IsAllowed;
        public string NetworkAccessButtonLabel => CompanionFirewall.NeedsRuleUpgrade
            ? "Update network access"
            : IsNetworkAccessAllowed ? "Network access allowed" : "Allow network access";

        [ObservableProperty]
        private ImageSource? _pairingQrCode;

        /// <summary>True once a phone has paired during this run of the wizard.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWaitingForPhone))]
        [NotifyPropertyChangedFor(nameof(PhoneHeadline))]
        private bool _isPhonePaired;

        public bool IsWaitingForPhone => !IsPhonePaired;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PhoneHeadline))]
        private string _pairedPhoneName = string.Empty;

        public string PhoneHeadline => IsPhonePaired ? PairedPhoneName : "No phone yet";

        [ObservableProperty]
        private string _phoneStatus = "Waiting for a phone to scan…";

        public SetupViewModel(RemoteCompanionService companion)
        {
            _companion = companion;
            _companion.DevicesChanged += OnDevicesChanged;

            // Two seconds is fast enough that the phone step feels live without the
            // wizard doing meaningful work while it sits open.
            _pairingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pairingTimer.Tick += OnPairingTimerTick;

            Restart();
        }

        /// <summary>
        /// Puts the wizard back on the first step with the machine's current settings.
        ///
        /// The view model is a singleton and Settings offers "Run setup again", so
        /// without this a second run would open on the finish panel of the first.
        /// </summary>
        public void Restart()
        {
            StopWatchingForPhone();

            // Reflect what is already configured rather than assuming a clean machine.
            _loadingChoices = true;
            try
            {
                RunAsAdministrator = AppConfig.Is("run_as_admin");
                RunOnStartup = Startup.IsScheduled() || !AppConfig.Is("setup_version");
                KeepChargeLimitAfterReboot = true;
                CheckUpdates = CanSelfUpdate && AppConfig.IsNotFalse("check_updates");
                ShowNotifications = AppConfig.IsNotFalse("toast_enabled");
                StopArmouryCrateServices = false;
            }
            finally
            {
                _loadingChoices = false;
            }

            _devicesAtStart = _companion.PairedDevices.Count;
            IsPhonePaired = false;
            PairedPhoneName = string.Empty;
            PhoneStatus = "Waiting for a phone to scan…";

            Tasks.Clear();
            _work.Clear();
            IsComplete = false;
            IsApplying = false;
            StatusMessage = string.Empty;

            // Forwards, so re-entering from the finish panel still reads as a start.
            Direction = 1;
            CurrentIndex = 0;
            SyncStepFlags();
        }

        public event Action? Completed;

        // ===== Navigation =====

        [RelayCommand]
        private void Next()
        {
            if (IsApplying) return;

            if (IsFinishStep)
            {
                Completed?.Invoke();
                return;
            }

            GoTo(CurrentIndex + 1);
        }

        [RelayCommand]
        private void Back()
        {
            if (IsApplying || !CanGoBack) return;
            GoTo(CurrentIndex - 1);
        }

        /// <summary>
        /// Rail navigation. Only steps already visited are reachable, so the rail cannot
        /// be used to jump past a question or into the applying step.
        /// </summary>
        [RelayCommand]
        private void GoToStep(object? indexParam)
        {
            if (IsApplying || IsFinishStep) return;
            if (indexParam is not int index)
            {
                if (!int.TryParse(indexParam?.ToString(), out index)) return;
            }

            // Count - 1 is the finish step, which is only ever reached through Next.
            if (index < 0 || index >= Steps.Count - 1 || index > CurrentIndex) return;

            GoTo(index);
        }

        private void GoTo(int index)
        {
            if (index == CurrentIndex) return;

            Direction = index > CurrentIndex ? 1 : -1;
            CurrentIndex = index;
            SyncStepFlags();

            // The pairing code and QR only need to be live while they are on screen.
            if (CurrentStage == SetupStage.Phone) StartWatchingForPhone();
            else StopWatchingForPhone();

            if (IsFinishStep) _ = Apply();
        }

        /// <summary>
        /// Jumps to a panel for the smoke harness, applying nothing.
        /// </summary>
        /// <remarks>
        /// <see cref="GoToStep"/> refuses a forward jump on purpose and starts the real
        /// work the moment the finish panel is reached, so a harness photographing the
        /// panels through it would register the startup task and stop services on the
        /// machine doing the rendering. This shows the finish panel in the state it ends
        /// in rather than producing that state.
        /// </remarks>
        public void PreviewStep(int index)
        {
            if (index < 0 || index >= Steps.Count) return;

            Direction = index >= CurrentIndex ? 1 : -1;
            CurrentIndex = index;
            SyncStepFlags();

            if (!IsFinishStep) return;

            BuildTaskList();
            foreach (SetupTask task in Tasks)
            {
                if (task.State == SetupTaskState.Pending) task.State = SetupTaskState.Done;
            }

            IsApplying = false;
            IsComplete = true;
            OnPropertyChanged(nameof(CompletionSummary));
        }

        private void SyncStepFlags()
        {
            foreach (SetupStep step in Steps)
            {
                step.IsActive = step.Index == CurrentIndex;
                step.IsDone = step.Index < CurrentIndex;
            }
        }

        [RelayCommand]
        private void Skip()
        {
            // Still record the version, otherwise setup reappears on every launch and
            // becomes something to dismiss rather than something to read.
            StopWatchingForPhone();
            AppConfig.Set("setup_version", CurrentSetupVersion);
            Completed?.Invoke();
        }

        [RelayCommand]
        private void RestartAsAdministrator() => ProcessHelper.RunAsAdmin();

        partial void OnRunAsAdministratorChanged(bool value)
        {
            if (_loadingChoices) return;

            AppConfig.Set("run_as_admin", value ? 1 : 0);
            if (!value || IsElevated) return;

            // Persist before relaunch so the elevated setup instance reflects the
            // accepted choice. Cancelled consent restores the toggle immediately.
            AppConfig.Flush();
            if (ProcessHelper.RunAsAdmin("--setup")) return;
            AppConfig.Set("run_as_admin", 0);
            _loadingChoices = true;
            RunAsAdministrator = false;
            _loadingChoices = false;
        }

        // ===== Applying =====

        private async Task Apply()
        {
            if (IsApplying || IsComplete) return;

            IsApplying = true;
            BuildTaskList();

            try
            {
                foreach (SetupTask task in Tasks)
                {
                    if (task.State == SetupTaskState.Skipped) continue;

                    task.State = SetupTaskState.Running;
                    StatusMessage = task.Label;

                    // The work itself is fast enough to finish between two frames. The
                    // pause is what makes the checklist read as a sequence rather than
                    // flashing complete the instant the panel appears.
                    Task delay = Task.Delay(260);
                    bool ok = await Task.Run(() => RunTask(task));
                    await delay;

                    task.State = ok ? SetupTaskState.Done : SetupTaskState.Failed;
                }

                await Task.Run(() => AppConfig.Set("setup_version", CurrentSetupVersion));
            }
            finally
            {
                IsApplying = false;
                StatusMessage = string.Empty;
                IsComplete = true;
                OnPropertyChanged(nameof(CompletionSummary));
            }
        }


        private void BuildTaskList()
        {
            Tasks.Clear();
            _work.Clear();

            Add(RunOnStartup ? "Register the startup task" : "Leave startup unchanged",
                SetupTaskState.Pending,
                () =>
                {
                    try
                    {
                        if (RunOnStartup) Startup.Schedule();
                        else if (!RunOnStartup) Startup.UnSchedule();
                        return true;
                    }
                    catch (Exception ex) { Logger.WriteLine("Setup startup task: " + ex.Message); return false; }
                });

            Add(KeepChargeLimitAfterReboot && IsElevated
                    ? "Keep the charge limit after a reboot"
                    : "Charge limit task left unchanged",
                KeepChargeLimitAfterReboot && !IsElevated ? SetupTaskState.Skipped : SetupTaskState.Pending,
                () =>
                {
                    try
                    {
                        if (KeepChargeLimitAfterReboot && IsElevated) Startup.ScheduleCharge();
                        else if (!KeepChargeLimitAfterReboot) Startup.UnscheduleCharge();
                        return true;
                    }
                    catch (Exception ex) { Logger.WriteLine("Setup charge task: " + ex.Message); return false; }
                });

            Add("Save notification and update preferences", SetupTaskState.Pending, () =>
            {
                AppConfig.Set("check_updates", CanSelfUpdate && CheckUpdates ? 1 : 0);
                AppConfig.Set("toast_enabled", ShowNotifications ? 1 : 0);
                return true;
            });

            if (StopArmouryCrateServices)
            {
                Add("Stop Armoury Crate background services", SetupTaskState.Pending, () =>
                {
                    try { AsusService.StopAsusServices(); return true; }
                    catch (Exception ex) { Logger.WriteLine("Setup ASUS services: " + ex.Message); return false; }
                });
            }

            if (IsPhonePaired)
            {
                var paired = new SetupTask("Phone paired") { Detail = PairedPhoneName, State = SetupTaskState.Done };
                Tasks.Add(paired);
            }
        }

        private void Add(string label, SetupTaskState initial, Func<bool> work)
        {
            var task = new SetupTask(label) { State = initial };
            Tasks.Add(task);
            _work[task] = work;
        }

        private bool RunTask(SetupTask task) => !_work.TryGetValue(task, out Func<bool>? work) || work();

        /// <summary>One line under the tick on the finish panel.</summary>
        public string CompletionSummary
        {
            get
            {
                int done = Tasks.Count(task => task.State == SetupTaskState.Done);
                int failed = Tasks.Count(task => task.State == SetupTaskState.Failed);

                if (failed > 0)
                    return $"{done} applied, {failed} could not be completed. Everything here is also in Settings.";

                return IsPhonePaired
                    ? $"Arsenal is configured and {PairedPhoneName} is paired. You can change any of this in Settings."
                    : "Arsenal is configured. You can change any of this in Settings.";
            }
        }

        // ===== Phone pairing =====

        private void StartWatchingForPhone()
        {
            RefreshPairing();
            _pairingTimer.Start();
        }

        private void StopWatchingForPhone() => _pairingTimer.Stop();

        private void OnPairingTimerTick(object? sender, EventArgs e) => RefreshPairing();

        private void OnDevicesChanged(object? sender, EventArgs e)
            => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshPairing);

        public void Dispose()
        {
            StopWatchingForPhone();
            _pairingTimer.Tick -= OnPairingTimerTick;
            _companion.DevicesChanged -= OnDevicesChanged;

            // Setup is over, so the pairing window it opened closes with it rather than
            // outliving the wizard by its full duration.
            _companion.ClosePairing();
            Completed = null;
            PairingQrCode = null;
        }

        private void RefreshPairing()
        {
            if (CompanionFirewall.IsAllowed && !_companion.IsRunning) _companion.Start();
            OnPropertyChanged(nameof(CompanionAddress));
            OnPropertyChanged(nameof(IsCompanionRunning));
            OnPropertyChanged(nameof(IsCompanionUnavailable));
            OnPropertyChanged(nameof(IsNetworkAccessAllowed));
            OnPropertyChanged(nameof(NetworkAccessButtonLabel));

            // Rendering the QR is not free, so only redo it when the code behind it has
            // actually rotated.
            string code = _companion.PairingCode;
            if (code != _renderedPairingCode)
            {
                _renderedPairingCode = code;
                OnPropertyChanged(nameof(CompanionPairingCode));
                OnPropertyChanged(nameof(CompanionFingerprint));
                OnPropertyChanged(nameof(CompanionFingerprintBlock));
                PairingQrCode = CreateQrCode(_companion.PairingUri);
            }

            IReadOnlyList<CompanionDeviceInfo> devices = _companion.PairedDevices;
            if (devices.Count <= _devicesAtStart)
            {
                if (!IsPhonePaired) PhoneStatus = "Waiting for a phone to scan…";
                return;
            }

            CompanionDeviceInfo newest = devices.OrderByDescending(device => device.PairedUtc).First();
            PairedPhoneName = newest.Name;
            IsPhonePaired = true;
            PhoneStatus = newest.IsOnline ? "Connected now" : "Paired, waiting for it to connect";
        }

        [RelayCommand]
        private async Task AllowCompanionNetwork()
        {
            PhoneStatus = ProcessHelper.IsUserAdministrator()
                ? "Adding private-network access…"
                : "Approve the Windows administrator prompt to allow private-network access.";

            bool started = ProcessHelper.IsUserAdministrator()
                ? await Task.Run(() => CompanionFirewall.AllowPrivateNetwork())
                : await CompanionFirewall.RequestAccessAsync();

            if (!started) PhoneStatus = "Network access was not changed.";
            else if (CompanionFirewall.IsAllowed)
            {
                _companion.Start();
                PhoneStatus = "Private-network access allowed.";
            }
            OnPropertyChanged(nameof(IsNetworkAccessAllowed));
            OnPropertyChanged(nameof(NetworkAccessButtonLabel));
        }

        [RelayCommand]
        private void RegeneratePairingCode()
        {
            _companion.RegeneratePairingCode();
            RefreshPairing();
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
    }
}
