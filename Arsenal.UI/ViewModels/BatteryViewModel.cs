using Arsenal.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace Arsenal.UI.ViewModels
{
    public partial class BatteryViewModel : ObservableObject
    {
        private readonly IBatteryService _batteryService;
        private readonly IDeviceStateService _deviceStateService;

        [ObservableProperty]
        private int _chargeLimit = 80;

        [ObservableProperty]
        private bool _isFullChargeOverride = false;

        [ObservableProperty]
        private int _batteryPercent = 80;

        [ObservableProperty]
        private float _dischargeRate = 0;

        [ObservableProperty]
        private string _batteryHealth = "Reading…";

        /// <summary>Capacity figures behind the health percentage.</summary>
        [ObservableProperty]
        private string _batteryHealthDetail = string.Empty;

        // ===== Power flow ======================================================
        // The rate is signed: positive is power going into the battery, negative is
        // power coming out. One tile reports whichever is happening, because a figure
        // labelled "discharge" while the machine is plugged in and filling up is just
        // wrong.

        public bool IsCharging => DischargeRate > 0;

        public string PowerFlowLabel => DischargeRate switch
        {
            > 0 => "Charging",
            < 0 => "Discharging",
            _ => "Battery power"
        };

        public string PowerFlowValue => $"{Math.Abs(DischargeRate):F1} W";

        public SymbolRegular PowerFlowIcon => IsCharging ? SymbolRegular.BatteryCharge24 : SymbolRegular.Flash24;

        /// <summary>
        /// The last draw seen while running on battery, in watts.
        /// </summary>
        /// <remarks>
        /// Kept so the runtime estimate still has something to divide by once the charger
        /// goes in. Both halves of the analysis are worth reading at the same time, and
        /// the discharge rate is unobservable at exactly the moment you are most likely
        /// to be asking how long a charge will last.
        /// </remarks>
        private float _lastDrawWatts;

        partial void OnDischargeRateChanged(float value)
        {
            if (value < 0) _lastDrawWatts = -value;
            RaisePowerFlowDependents();
        }

        partial void OnBatteryPercentChanged(int value) => RaisePowerFlowDependents();

        private void RaisePowerFlowDependents()
        {
            OnPropertyChanged(nameof(IsCharging));
            OnPropertyChanged(nameof(PowerFlowLabel));
            OnPropertyChanged(nameof(PowerFlowValue));
            OnPropertyChanged(nameof(PowerFlowIcon));
            OnPropertyChanged(nameof(TimeToFullText));
            OnPropertyChanged(nameof(TimeToEmptyText));
            OnPropertyChanged(nameof(ChargeStopsAtText));
        }

        /// <summary>Watt-hours in the battery now, or null if it will not say.</summary>
        private static decimal? StoredWh =>
            HardwareControl.chargeCapacity > 0 ? HardwareControl.chargeCapacity / 1000 : null;

        /// <summary>Watt-hours the battery holds when full, allowing for wear.</summary>
        private static decimal? CapacityWh =>
            HardwareControl.fullCapacity > 0 ? HardwareControl.fullCapacity / 1000 : null;

        /// <summary>
        /// How long until charging stops. Measured against the level the limit will
        /// actually stop at, not 100%, so a machine capped at 80% is not reported as
        /// still having an hour to go once it has finished.
        /// </summary>
        public string TimeToFullText
        {
            get
            {
                if (StoredWh is not { } stored || CapacityWh is not { } capacity) return "Charge time unavailable";

                decimal target = capacity * EffectiveChargeLimit / 100;
                decimal missing = target - stored;
                if (missing <= 0) return "Charged to the limit";
                if (DischargeRate <= 0) return $"{FormatWh(missing)} below the limit";

                return "Full in " + FormatHours((double)(missing / (decimal)DischargeRate));
            }
        }

        /// <summary>How long the charge lasts at the draw being measured, or the last one.</summary>
        public string TimeToEmptyText
        {
            get
            {
                if (StoredWh is not { } stored) return string.Empty;

                float draw = DischargeRate < 0 ? -DischargeRate : _lastDrawWatts;
                if (draw <= 0) return "Runtime measured once on battery";

                string estimate = FormatHours((double)(stored / (decimal)draw));
                return DischargeRate < 0
                    ? estimate + " of use left"
                    : estimate + $" of use at {draw:F1} W";
            }
        }

        private static string FormatWh(decimal wattHours) => $"{wattHours:F1} Wh";

        private static string FormatHours(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours) || hours <= 0) return "moments";
            if (hours >= 48) return "over 2 days";

            int whole = (int)hours;
            int minutes = (int)Math.Round((hours - whole) * 60);
            if (minutes == 60) { whole++; minutes = 0; }

            if (whole == 0) return $"{minutes} min";
            return minutes == 0 ? $"{whole} h" : $"{whole} h {minutes} min";
        }

        /// <summary>
        /// Some models only accept 60-80 and 100 rather than a free range; the backend
        /// rounds anything else to one of those. Surfacing the same rule here means the
        /// slider offers what the hardware will honour instead of a value it will
        /// silently change.
        /// </summary>
        public bool HasSteppedChargeLimit { get; } = AppConfig.IsChargeLimit6080();

        public int ChargeLimitMinimum => HasSteppedChargeLimit ? 60 : 40;

        /// <summary>
        /// Positions the thumb may take. Snapping to the nearest listed value keeps the
        /// gap between 80 and 100 crossable in both directions; coercing after the fact
        /// would strand the thumb at 100.
        /// </summary>
        public System.Windows.Media.DoubleCollection ChargeLimitTicks { get; }

        /// <summary>Values labelled on the scale under the track.</summary>
        public System.Windows.Media.DoubleCollection ChargeLimitMarks { get; }

        /// <summary>
        /// The level charging will actually stop at right now, accounting for a
        /// one-time full charge overriding the saved limit.
        /// </summary>
        public int EffectiveChargeLimit => IsFullChargeOverride ? 100 : ChargeLimit;

        public string ChargeStopsAtText
            => BatteryPresentation.ChargeStopsAtText(BatteryPercent, EffectiveChargeLimit);

        partial void OnChargeLimitChanged(int value) => RaiseChargeLimitDependents();
        partial void OnIsFullChargeOverrideChanged(bool value) => RaiseChargeLimitDependents();

        private void RaiseChargeLimitDependents()
        {
            OnPropertyChanged(nameof(EffectiveChargeLimit));
            OnPropertyChanged(nameof(ChargeStopsAtText));
            RaisePowerFlowDependents();
        }

        public string ChargeLimitNote => HasSteppedChargeLimit
            ? "This model charges to any level between 60% and 80%, or to 100%. There are no stops in between."
            : "Any level between 40% and 100%.";

        public BatteryViewModel(IBatteryService batteryService, IDeviceStateService deviceStateService)
        {
            if (HasSteppedChargeLimit)
            {
                var ticks = new System.Windows.Media.DoubleCollection();
                for (int value = 60; value <= 80; value++) ticks.Add(value);
                ticks.Add(100);
                ticks.Freeze();
                ChargeLimitTicks = ticks;
                var marks = new System.Windows.Media.DoubleCollection { 60, 80, 100 };
                marks.Freeze();
                ChargeLimitMarks = marks;
            }
            else
            {
                var ticks = new System.Windows.Media.DoubleCollection();
                ticks.Freeze();
                ChargeLimitTicks = ticks;
                var marks = new System.Windows.Media.DoubleCollection { 40, 100 };
                marks.Freeze();
                ChargeLimitMarks = marks;
            }

            _batteryService = batteryService;
            _deviceStateService = deviceStateService;

            ChargeLimit = _batteryService.ChargeLimit;
            IsFullChargeOverride = _batteryService.IsFullChargeOverride;
            BatteryPercent = _batteryService.BatteryPercent;
            DischargeRate = _batteryService.DischargeRateWatts;

            _deviceStateService.TelemetryUpdated += (t) =>
            {
                // Nothing ever asked the hardware layer for a health figure, so
                // batteryHealth sat at its -1 sentinel and the tile read "Reading…"
                // forever. The original refreshes it on demand, throttled.
                RequestBatteryHealthRefresh();

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    BatteryPercent = t.BatteryPercentage;
                    DischargeRate = t.BatteryDischargeRate;
                    UpdateBatteryHealthText();
                });
            };
            _batteryService.ChargeLimitChanged += limit => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ChargeLimit = limit);
            _batteryService.FullChargeOverrideChanged += enabled => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => IsFullChargeOverride = enabled);
        }

        /// <summary>
        /// Reporting the limit to Windows writes under HKLM, which needs elevation. The
        /// firmware limit itself applies either way, so this is only about the battery
        /// icon's smart-charging indicator.
        /// </summary>
        public bool NeedsElevation { get; } = !Arsenal.Helpers.ProcessHelper.IsUserAdministrator();

        [RelayCommand]
        public void RestartAsAdministrator() => Arsenal.Helpers.ProcessHelper.RunAsAdmin();

        private long _lastHealthRefresh;
        private int _healthRefreshRunning;

        /// <summary>
        /// Design and full-charge capacity come from WMI, which is slow, and wear does
        /// not move minute to minute - so this re-reads at most every 15 minutes, the
        /// same interval the original uses, and always off the UI thread.
        /// </summary>
        private void RequestBatteryHealthRefresh()
        {
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (_lastHealthRefresh != 0 && Math.Abs(now - _lastHealthRefresh) <= 15 * 60_000) return;
            if (Interlocked.Exchange(ref _healthRefreshRunning, 1) == 1) return;

            _lastHealthRefresh = now;
            Task.Run(() =>
            {
                try { HardwareControl.RefreshBatteryHealth(); }
                catch (Exception ex) { Logger.WriteLine("Battery health: " + ex.Message); }
                finally { Interlocked.Exchange(ref _healthRefreshRunning, 0); }

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateBatteryHealthText);
            });
        }

        private void UpdateBatteryHealthText()
        {
            if (HardwareControl.batteryHealth > 0)
            {
                BatteryHealth = $"{HardwareControl.batteryHealth:F0}%";
                BatteryHealthDetail = HardwareControl.fullCapacity > 0 && HardwareControl.designCapacity > 0
                    ? $"{HardwareControl.fullCapacity / 1000:F1} of {HardwareControl.designCapacity / 1000:F1} Wh"
                    : "Full charge vs original capacity";
            }
            else
            {
                BatteryHealth = "Unavailable";
                BatteryHealthDetail = "This battery does not report its capacity";
            }
        }

        [RelayCommand]
        public void SetLimit(object? limitParam)
        {
            int limit = ToInt(limitParam, 80);
            ChargeLimit = limit;
            _batteryService.SetChargeLimit(limit);
        }

        [RelayCommand]
        public void ToggleFullCharge()
        {
            _batteryService.ToggleFullChargeOverride();
            IsFullChargeOverride = _batteryService.IsFullChargeOverride;
        }

        // ---- The battery report, read in the app ---------------------------------

        [ObservableProperty] private bool _isBuildingReport;
        [ObservableProperty] private bool _hasReport;
        [ObservableProperty] private string _reportError = "";

        [ObservableProperty] private string _reportHealth = "";
        [ObservableProperty] private string _reportHealthDetail = "";
        [ObservableProperty] private string _reportCapacity = "";
        [ObservableProperty] private string _reportCapacityDetail = "";
        [ObservableProperty] private string _reportCycles = "";
        [ObservableProperty] private string _reportCyclesDetail = "";
        [ObservableProperty] private string _reportRuntime = "";
        [ObservableProperty] private string _reportRuntimeDetail = "";
        [ObservableProperty] private string _reportChemistry = "";
        [ObservableProperty] private string _reportScanned = "";
        [ObservableProperty] private string _reportHistoryCaption = "";

        /// <summary>The weekly capacity readings, oldest first, for the chart.</summary>
        public System.Collections.ObjectModel.ObservableCollection<Arsenal.Battery.BatteryHistoryPoint> ReportHistory { get; } = new();

        private string _reportHtmlPath = "";

        public bool CanOpenFullReport => _reportHtmlPath.Length > 0;

        /// <summary>
        /// Builds the report and reads it here rather than handing the user a web page.
        /// </summary>
        /// <remarks>
        /// The scan takes a few seconds and runs a process, so it is off the UI thread
        /// and the button says so while it runs. Pressing it again while one is in
        /// flight is a no-op rather than a second powercfg.
        /// </remarks>
        [RelayCommand]
        public async Task GenerateReportAsync()
        {
            if (IsBuildingReport) return;

            IsBuildingReport = true;
            ReportError = "";

            try
            {
                Arsenal.Battery.BatteryReportData? report = await _batteryService.BuildBatteryReportAsync();
                if (report is null)
                {
                    ReportError = "Windows could not produce a battery report on this machine. The log has the detail.";
                    HasReport = false;
                    return;
                }

                Apply(report);
                HasReport = true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Battery report failed: " + ex.Message);
                ReportError = "The battery report could not be read. The log has the detail.";
                HasReport = false;
            }
            finally
            {
                IsBuildingReport = false;
            }
        }

        private void Apply(Arsenal.Battery.BatteryReportData report)
        {
            ReportHealth = $"{report.Health * 100:F0}%";
            ReportHealthDetail = report.WearPercent < 1
                ? "No measurable wear yet"
                : $"{report.WearPercent:F0}% of the original capacity has been lost";

            ReportCapacity = $"{report.FullChargeCapacity / 1000.0:F1} Wh";
            ReportCapacityDetail = $"Built to hold {report.DesignCapacity / 1000.0:F1} Wh";

            // A pack that will not report its cycles says 0, which is not none.
            ReportCycles = report.HasCycleCount ? report.CycleCount.ToString() : "Not reported";
            ReportCyclesDetail = report.HasCycleCount
                ? "Full charge and discharge cycles"
                : "This battery's firmware does not publish a cycle count";

            ReportRuntime = report.CurrentRuntime is { } current ? FormatHours(current.TotalHours) : "Unknown";
            ReportRuntimeDetail = report.DesignRuntime is { } design
                ? $"{FormatHours(design.TotalHours)} when the battery was new"
                : "Estimated from this machine's own use, not from a benchmark";

            ReportChemistry = string.IsNullOrWhiteSpace(report.Chemistry) ? "Unknown" : report.Chemistry;
            ReportScanned = $"Scanned {report.ScanTime:d MMMM yyyy, HH:mm}";

            ReportHistory.Clear();
            foreach (Arsenal.Battery.BatteryHistoryPoint point in report.History) ReportHistory.Add(point);

            ReportHistoryCaption = report.History.Count >= 2
                ? DescribeHistory(report)
                : "Windows has not recorded enough weeks on this machine to show a trend yet.";

            _reportHtmlPath = report.HtmlPath;
            OnPropertyChanged(nameof(CanOpenFullReport));
        }

        private static string DescribeHistory(Arsenal.Battery.BatteryReportData report)
        {
            int lost = report.CapacityLostOverReport;
            string span = $"{report.History[0].Period:MMM yyyy} to {report.History[^1].Period:MMM yyyy}";

            // A pack can report more than it did a year ago, usually after the gauge
            // recalibrates. Saying it "gained" capacity would be wrong, so say what
            // actually happened.
            if (lost <= 0)
                return $"{span}. The reported capacity is no lower than when the record starts.";

            return $"{span}. {lost / 1000.0:F1} Wh lower than when the record starts.";
        }

        /// <summary>
        /// Writes the Windows page and opens it.
        /// </summary>
        /// <remarks>
        /// Written here rather than alongside the scan. It is a second pass of the same
        /// work and a second file on disk for something the summary above it has already
        /// answered, so it is paid for by whoever asks for it.
        /// </remarks>
        [RelayCommand]
        public async Task OpenFullReportAsync()
        {
            if (_reportHtmlPath.Length == 0) return;

            try
            {
                string page = await Arsenal.Battery.BatteryReportReader.WriteFullReportAsync(_reportHtmlPath);
                if (page.Length == 0)
                {
                    ReportError = "Windows could not write the full report page. The log has the detail.";
                    return;
                }

                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }
        }

        private static int ToInt(object? param, int defaultValue = 80)
        {
            if (param == null) return defaultValue;
            if (param is int i) return i;
            if (int.TryParse(param.ToString(), out int parsed)) return parsed;
            return defaultValue;
        }
    }
}
