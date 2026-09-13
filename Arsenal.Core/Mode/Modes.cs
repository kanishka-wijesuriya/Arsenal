namespace Arsenal.Mode
{
    public class Modes
    {
        static Dictionary<string, string> settings = new Dictionary<string, string>
        {
            { "mode_base", "_" },
            { "mode_name", "_" },
            { "powermode", "string" },
            { "limit_total", "int" },
            { "limit_slow", "int" },
            { "limit_fast", "int" },
            { "limit_cpu", "int" },
            { "limit_crossload", "int" },
            { "limit_gpucpu", "int" },
            { "limit_cputemp", "int" },
            { "fan_profile_cpu", "string" },
            { "fan_profile_gpu", "string" },
            { "fan_profile_mid", "string" }, 
            { "gpu_power", "int" },
            { "gpu_boost", "int" },
            { "gpu_temp", "int" },
            { "gpu_core", "int" },
            { "gpu_memory", "int" },
            { "gpu_clock_limit", "int" },
            { "cpu_temp", "_" },
            { "cpu_uv", "_" },
            { "cpu_uv_cores", "_" },
            { "igpu_uv", "_" },
            { "auto_boost", "int" },
            { "auto_apply", "int" },
            { "auto_apply_power", "int" },
            { "auto_uv", "_" },
            { "hysteresis_up", "int" },
            { "hysteresis_down", "int" }
        };

        public const int MaxModes = 20;

        public static Dictionary<int, string> GetDictonary()
        {
            Dictionary<int, string> modes = new Dictionary<int, string>
            {
              {2, GetName(2)},
              {0, GetName(0)},
              {1, GetName(1)}
            };

            for (int i = 3; i < MaxModes; i++)
            {
                if (Exists(i)) modes.Add(i, GetName(i));
            }

            return modes;
        }

        public static List<int> GetList()
        {
            List<int> modes = new() { 2, 0, 1 };
            for (int i = 3; i < MaxModes; i++)
            {
                if (Exists(i)) modes.Add(i);
            }

            return modes;
        }

        public static void Remove(int mode)
        {
            foreach (string clean in settings.Keys)
            {
                AppConfig.Remove(clean + "_" + mode);
            }
        }

        public static int Add(string? requestedName = null)
        {
            int currentMode = GetCurrent();

            for (int i = 3; i < MaxModes; i++)
            {
                if (Exists(i)) continue;

                AppConfig.Set("mode_base_" + i, GetCurrentBase());
                AppConfig.Set("mode_name_" + i, NormaliseName(requestedName, NextDefaultName()));

                if (Exists(currentMode))
                {
                    foreach (var kvp in settings)
                    {
                        if (kvp.Value == "_") continue;

                        string sourceKey = kvp.Key + "_" + currentMode;
                        string targetKey = kvp.Key + "_" + i;

                        if (!AppConfig.Exists(sourceKey)) continue;

                        if (kvp.Value == "int")
                            AppConfig.Set(targetKey, AppConfig.Get(sourceKey));
                        else
                            AppConfig.Set(targetKey, AppConfig.GetString(sourceKey));
                    }
                }

                return i;
            }
            return -1;
        }

        public static bool Rename(int mode, string name)
        {
            if (mode <= 2 || !Exists(mode)) return false;
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0) return false;
            AppConfig.Set("mode_name_" + mode, trimmed.Length > 48 ? trimmed[..48] : trimmed);
            return true;
        }

        private static string NextDefaultName()
        {
            HashSet<string> names = GetList().Select(GetName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int number = 1; number <= MaxModes; number++)
            {
                string candidate = $"Custom Plan {number}";
                if (!names.Contains(candidate)) return candidate;
            }
            return "Custom Plan";
        }

        private static string NormaliseName(string? name, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
            return value.Length > 48 ? value[..48] : value;
        }

        public static void InitFullSpeed()
        {
            int vivoMode = Program.acpi.DeviceGet(AsusACPI.VivoBookMode);
            if (vivoMode < 0) return;
            Logger.WriteLine($"VivoBookMode: {vivoMode} (0x{vivoMode:X})");
            if ((vivoMode & 0x40000) == 0) return;

            for (int i = 3; i < MaxModes; i++)
                if (GetBase(i) == AsusACPI.PerformanceFullSpeed) return;

            for (int i = 3; i < MaxModes; i++)
            {
                if (Exists(i)) continue;
                AppConfig.Set("mode_base_" + i, AsusACPI.PerformanceFullSpeed);
                AppConfig.Set("mode_name_" + i, "Full Speed");
                return;
            }
        }

        public static int GetCurrent()
        {
            return AppConfig.Get("performance_mode");
        }

        public static bool IsCurrentCustom()
        {
            return GetCurrent() > 2;
        }

        public static void SetCurrent(int mode)
        {
            AppConfig.Set("performance_" + Program.PerformanceKey(), mode);
            AppConfig.Set("performance_mode", mode);
        }

        public static int GetCurrentBase()
        {
            return GetBase(GetCurrent());
        }

        public static string GetCurrentName()
        {
            return GetName(GetCurrent());
        }

        public static bool Exists(int mode)
        {
            return GetBase(mode) >= 0;
        }

        public static int GetBase(int mode)
        {
            if (mode >= 0 && mode <= 2)
                return mode;
            else
                return AppConfig.Get("mode_base_" + mode);
        }

        public static string GetName(int mode)
        {
            try
            {
                switch (mode)
                {
                    case 0:
                        return "Balanced";
                    case 1:
                        return "Turbo";
                    case 2:
                        return "Silent";
                    default:
                        return AppConfig.GetString("mode_name_" + mode) ?? ("Custom " + mode);
                }
            }
            catch
            {
                return mode switch
                {
                    0 => "Balanced",
                    1 => "Turbo",
                    2 => "Silent",
                    _ => "Custom " + mode
                };
            }
        }


        public static int GetNext(bool back = false)
        {
            var modes = GetList();
            int index = modes.IndexOf(GetCurrent());

            if (back)
            {
                index--;
                if (index < 0) index = modes.Count - 1;
                return modes[index];
            }
            else
            {
                index++;
                if (index > modes.Count - 1) index = 0;
                return modes[index];
            }
        }
    }
}
