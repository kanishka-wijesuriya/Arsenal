using Arsenal.Ally;
using System.Diagnostics;
using System.Management;

namespace Arsenal.Helpers
{
    public static class AsusService
    {
        /// <summary>Busy state, headline and current service detail.</summary>
        public static event Action<bool, string, string>? TransitionChanged;

        static List<string> services = new() {
                "ArmouryCrateControlInterface",
                "ArmouryCrateProArtService",
                "AsHidService",
                "ASUSOptimization",
                "AsusAppService",
                "ASUSLinkNear",
                "ASUSLinkRemote",
                "ASUSSoftwareManager",
                "ASUSLiveUpdateAgent",
                "ASUSSwitch",
                "ASUSSystemAnalysis",
                "ASUSSystemDiagnosis",
                "ASUSXGMobileService",
                "AsusCertService"
        };

        //"AsusPTPService",

        static List<string> servicesAC = new() {
                "ArmouryCrateSEService",
                "ArmouryCrateService",
                "LightingService",
        };

        private static bool IsRunning(string name)
        {
            var procs = Process.GetProcessesByName(name);
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }

        public static bool IsAsusOptimizationRunning() => IsRunning("AsusOptimization");

        public static bool IsArmouryRunning()
        {
            var acService = IsRunning("ArmouryCrate.Service");
            var lightingService = IsRunning("LightingService");
            Logger.WriteLine($"AC Service: {acService}, Lighting Service: {lightingService}");
            return acService || lightingService;
        }

        public static void RunArmouryUninstaller()
        {
            Process.Start(new ProcessStartInfo("https://dlcdnets.asus.com/pub/ASUS/mb/14Utilities/Armoury_Crate_Uninstall_Tool.zip") { UseShellExecute = true });
        }

        public static bool IsOSDRunning() => IsRunning("AsusOSD");


        private static Dictionary<string, string> GetServiceStates()
        {
            var names = AppConfig.IsStopAC() ? services.Concat(servicesAC) : services;
            var states = new Dictionary<string, string>();
            try
            {
                string filter = string.Join(" OR ", names.Select(name => $"Name='{name}'"));
                using var searcher = new ManagementObjectSearcher($"SELECT Name, State FROM Win32_Service WHERE {filter}");
                foreach (ManagementObject mo in searcher.Get())
                    states[(string)mo["Name"]] = (string)mo["State"];
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }
            return states;
        }

        private static List<string> GetRunningServices()
        {
            return GetServiceStates().Where(s => s.Value != "Stopped").Select(s => s.Key).ToList();
        }

        public static int GetRunningCount()
        {
            return GetRunningServices().Count;
        }


        public static void StopAsusServices()
        {
            List<string> running = GetRunningServices();
            TransitionChanged?.Invoke(true, "Stopping ASUS services",
                running.Count == 0 ? "No running ASUS services were found" : $"Preparing {running.Count} services…");
            try
            {
                for (int index = 0; index < running.Count; index++)
                {
                    string service = running[index];
                    TransitionChanged?.Invoke(true, "Stopping ASUS services",
                        $"Stopping {DisplayName(service)} · {index + 1} of {running.Count}");
                    ProcessHelper.StopDisableService(service, servicesAC.Contains(service) ? "Manual" : "Disabled");
                }

                if (GetRunningCount() == 0) AppConfig.Set("services_disabled", 1);

                if (AppConfig.IsAlly()) AllyControl.ApplyMode((ControllerMode)AppConfig.Get("controller_mode", (int)ControllerMode.Auto), true);
            }
            finally
            {
                TransitionChanged?.Invoke(false, "ASUS services stopped",
                    running.Count == 0 ? "Nothing needed to be stopped" : $"Stopped {running.Count} services");
            }
        }

        public static void StartAsusServices()
        {
            List<string> installed = GetServiceStates().Keys.ToList();
            TransitionChanged?.Invoke(true, "Starting ASUS services",
                installed.Count == 0 ? "No installed ASUS services were found" : $"Preparing {installed.Count} services…");
            try
            {
                AppConfig.Set("services_disabled", 0);
                for (int index = 0; index < installed.Count; index++)
                {
                    string service = installed[index];
                    TransitionChanged?.Invoke(true, "Starting ASUS services",
                        $"Starting {DisplayName(service)} · {index + 1} of {installed.Count}");
                    ProcessHelper.StartEnableService(service);
                }
            }
            finally
            {
                TransitionChanged?.Invoke(false, "ASUS services started",
                    installed.Count == 0 ? "Nothing needed to be started" : $"Started {installed.Count} services");
            }
        }

        private static string DisplayName(string service) => service switch
        {
            "ArmouryCrateControlInterface" => "Armoury Crate Control Interface",
            "ArmouryCrateProArtService" => "Armoury Crate ProArt Service",
            "ArmouryCrateSEService" => "Armoury Crate SE Service",
            "ArmouryCrateService" => "Armoury Crate Service",
            "AsHidService" => "ASUS HID Service",
            "ASUSOptimization" => "ASUS Optimization",
            "AsusAppService" => "ASUS App Service",
            "ASUSLinkNear" => "ASUS Link Near",
            "ASUSLinkRemote" => "ASUS Link Remote",
            "ASUSSoftwareManager" => "ASUS Software Manager",
            "ASUSLiveUpdateAgent" => "ASUS Live Update Agent",
            "ASUSSwitch" => "ASUS Switch",
            "ASUSSystemAnalysis" => "ASUS System Analysis",
            "ASUSSystemDiagnosis" => "ASUS System Diagnosis",
            "ASUSXGMobileService" => "ASUS XG Mobile Service",
            "AsusCertService" => "ASUS Certificate Service",
            "LightingService" => "Armoury Crate Lighting Service",
            _ => service
        };

        public static void StopOnStartup()
        {
            if (AppConfig.Is("services_skip")) return;
            if (!AppConfig.Is("services_disabled") || !ProcessHelper.IsUserAdministrator()) return;
            if (GetRunningCount() == 0) return;

            Logger.WriteLine("ASUS services revived, re-stopping on startup");
            Task.Run(() => StopAsusServices());
        }

    }

}
