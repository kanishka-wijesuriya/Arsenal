using Arsenal.Application.Models;
using Arsenal.Helpers;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Arsenal.Application.Services.Implementations
{
    /// <summary>
    /// Resolves ASUS support packages against the same sources Windows and the legacy
    /// app use: present devnodes, extension-driver metadata, staged INFs, ASUS package
    /// registry entries and Store registrations. Display-name guessing is deliberately
    /// not used because similarly named WLAN/LAN packages routinely produce false hits.
    /// </summary>
    internal static class AsusDriverDetector
    {
        private sealed record LocalDriver(string MatchId, string Version, bool IsExtension, string Entry);

        public static void Resolve(IList<UpdateInfo> updates, CancellationToken token = default)
        {
            List<LocalDriver> inventory = BuildInventory();
            var installed = new string?[updates.Count];
            var stagedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < updates.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                installed[index] = ResolveInstalledVersion(inventory, updates[index]);
                if (installed[index] is null)
                    foreach (string id in updates[index].HardwareIds) stagedIds.Add(id);
            }

            HideAbsentAdapterAlternatives(updates, inventory, installed, stagedIds);
            Dictionary<string, string>? staged = stagedIds.Count == 0 ? null : BuildStagedVersions(stagedIds, token);
            HashSet<string>? storePackages = null;

            for (int index = 0; index < updates.Count; index++)
            {
                UpdateInfo item = updates[index];
                if (item.IsHidden) continue;

                string? version = installed[index];
                if (version is null && staged is not null && item.HardwareIds.Count > 0)
                {
                    version = MaxVersion(item.HardwareIds
                        .Where(staged.ContainsKey)
                        .Select(id => staged[id])
                        .Where(value => Major(value) == Major(item.LatestVersion)));
                }
                if (version is null && item.HardwareIds.Count == 0)
                    version = FindStagedVersionByTitle(item.Title, item.LatestVersion, token);

                if (!string.IsNullOrWhiteSpace(version))
                {
                    item.InstalledVersion = version;
                    item.State = CompareVersions(version, item.LatestVersion);
                    item.IsUpdateAvailable = item.State == UpdateState.Outdated;
                    Logger.WriteLine($"ASUS driver {item.Title}: available {item.LatestVersion}, installed {version}, {item.State}");
                }
                else if (item.LatestVersion.Contains("store", StringComparison.OrdinalIgnoreCase)
                         && IsStoreAppInstalled(storePackages ??= GetInstalledPackages(), item.Title))
                {
                    item.InstalledVersion = "Microsoft Store";
                    item.State = UpdateState.UpToDate;
                }
                else
                {
                    item.State = UpdateState.NotInstalled;
                }
            }
        }

        private static string? ResolveInstalledVersion(List<LocalDriver> inventory, UpdateInfo item)
        {
            if (item.HardwareIds.Count > 0)
            {
                List<LocalDriver> matched = inventory
                    .Where(driver => item.HardwareIds.Any(id => driver.MatchId.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (matched.Count == 0) return null;

                int major = Major(item.LatestVersion);
                List<LocalDriver> pool = matched.Where(driver => Major(driver.Version) == major).ToList();
                if (pool.Count == 0) pool = matched.Where(driver => !driver.IsExtension).ToList();
                if (pool.Count == 0) pool = matched;
                return MaxVersion(pool.Select(driver => driver.Version));
            }

            // Some ASUS extension packages do not carry HardwareInfoList. The extension
            // metadata and ASUS registry name are the only stable identifiers for them.
            string key = item.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (key.Length < 3) return null;
            return MaxVersion(inventory
                .Where(driver => driver.IsExtension
                    && Major(driver.Version) == Major(item.LatestVersion)
                    && driver.Entry.Contains(key, StringComparison.OrdinalIgnoreCase))
                .Select(driver => driver.Version));
        }

        private static void HideAbsentAdapterAlternatives(
            IList<UpdateInfo> updates,
            List<LocalDriver> inventory,
            string?[] installed,
            HashSet<string> stagedIds)
        {
            string[][] adapterGroups =
            {
                new[] { "Bluetooth" },
                new[] { "WLAN", "Wireless LAN", "Wi-Fi", "WiFi" },
                new[] { "Realtek LAN" },
                new[] { "Card Reader", "CardReader" }
            };

            foreach (string[] keys in adapterGroups)
            {
                List<int> members = Enumerable.Range(0, updates.Count)
                    .Where(index => updates[index].HardwareIds.Count > 0
                        && keys.Any(key => updates[index].Title.Contains(key, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (members.Count < 2) continue;

                LocalDriver? detected = inventory
                    .Where(driver => !driver.IsExtension && keys.Any(key => driver.Entry.Contains(key, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(driver => driver.Entry.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (!members.Any(index => installed[index] is not null) && detected is null) continue;

                foreach (int index in members.Where(index => installed[index] is null))
                {
                    updates[index].IsHidden = true;
                    foreach (string id in updates[index].HardwareIds) stagedIds.Remove(id);
                }
            }
        }

        private static List<LocalDriver> BuildInventory()
        {
            var result = new List<LocalDriver>();
            foreach (string deviceId in GetPresentDeviceIds())
            {
                if (CM_Locate_DevNodeW(out uint devInst, deviceId, 0) != 0) continue;

                string? version = PropertyString(GetDevNodeProperty(devInst, DeviceDriverVersion));
                if (!string.IsNullOrWhiteSpace(version))
                {
                    string description = PropertyString(GetDevNodeProperty(devInst, DeviceDescription)) ?? string.Empty;
                    foreach (string hardwareId in PropertyList(GetDevNodeProperty(devInst, DeviceHardwareIds)))
                        result.Add(new LocalDriver(CleanupDeviceId(hardwareId), version, false, description));
                }

                foreach (string entry in PropertyList(GetDevNodeProperty(devInst, DeviceExtendedConfigurationIds)))
                {
                    int colon = entry.IndexOf(':');
                    if (colon < 0) continue;
                    string[] parts = entry[(colon + 1)..].Split(',');
                    if (parts.Length >= 2 && ParseVersion(parts[^1]) is not null)
                        result.Add(new LocalDriver(CleanupDeviceId(parts[0]), parts[^1], true, entry));
                }
            }

            AddAsusInstalledVersions(result);
            return result;
        }

        private static void AddAsusInstalledVersions(List<LocalDriver> result)
        {
            try
            {
                using RegistryKey? asus = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASUS");
                if (asus is null) return;
                foreach (string name in asus.GetSubKeyNames())
                {
                    using RegistryKey? sub = asus.OpenSubKey(name);
                    if (sub?.GetValue("DisplayVersion") is string version && ParseVersion(version) is not null)
                        result.Add(new LocalDriver(string.Empty, version, true, name));
                }
            }
            catch (Exception ex) { Logger.WriteLine("ASUS registry inventory: " + ex.Message); }
        }

        private static Dictionary<string, string> BuildStagedVersions(HashSet<string> hardwareIds, CancellationToken token)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string infFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            string[] files;
            try { files = Directory.GetFiles(infFolder, "oem*.inf"); }
            catch (Exception ex) { Logger.WriteLine("Driver store inventory: " + ex.Message); return result; }

            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                string text;
                try
                {
                    // Modern display INFs are routinely several megabytes (NVIDIA's
                    // ASUS package is over 3 MB). The legacy 1 MB guard skipped those
                    // staged drivers entirely and reported them as not installed while
                    // the dGPU was disabled. Keep a defensive ceiling, but high enough
                    // for current DCH packages.
                    if (new FileInfo(file).Length > 16 * 1024 * 1024) continue;
                    text = File.ReadAllText(file);
                }
                catch { continue; }

                if (!hardwareIds.Any(id => text.Contains(id, StringComparison.OrdinalIgnoreCase))) continue;
                string version = Regex.Match(text, @"DriverVer\s*=[^,\r\n]*,\s*([0-9][0-9.]*)").Groups[1].Value;
                if (ParseVersion(version) is null) continue;

                foreach (string id in hardwareIds.Where(id => text.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    if (!result.TryGetValue(id, out string? current) || CompareVersionValues(version, current) > 0)
                        result[id] = version;
            }
            return result;
        }

        /// <summary>
        /// ASUS omits HardwareInfoList for a few component bundles such as Dolby. Their
        /// staged INF still carries both the provider name and package version, so use
        /// that evidence rather than reporting a present component as not installed.
        /// </summary>
        private static string? FindStagedVersionByTitle(string title, string available, CancellationToken token)
        {
            string key = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (key.Length < 4) return null;

            string infFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            string[] files;
            try { files = Directory.GetFiles(infFolder, "oem*.inf"); }
            catch { return null; }

            var versions = new List<string>();
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                string text;
                try
                {
                    if (new FileInfo(file).Length > 16 * 1024 * 1024) continue;
                    text = File.ReadAllText(file);
                }
                catch { continue; }

                if (!text.Contains(key, StringComparison.OrdinalIgnoreCase)) continue;
                string version = Regex.Match(text, @"DriverVer\s*=[^,\r\n]*,\s*([0-9][0-9.]*)").Groups[1].Value;
                if (Major(version) == Major(available)) versions.Add(version);
            }
            return MaxVersion(versions);
        }

        private static HashSet<string> GetInstalledPackages()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                if (key is not null)
                    foreach (string name in key.GetSubKeyNames()) result.Add(name);
            }
            catch (Exception ex) { Logger.WriteLine("Store package inventory: " + ex.Message); }
            return result;
        }

        private static bool IsStoreAppInstalled(HashSet<string> packages, string title)
        {
            string key = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            return key.Length >= 3 && packages.Any(package => package.Contains(key, StringComparison.OrdinalIgnoreCase));
        }

        private static UpdateState CompareVersions(string installed, string available)
        {
            if (ParseVersion(installed) is null || ParseVersion(available) is null)
                return UpdateState.Unknown;
            int comparison = CompareVersionValues(installed, available);
            return comparison < 0 ? UpdateState.Outdated
                : comparison > 0 ? UpdateState.Newer
                : UpdateState.UpToDate;
        }

        private static int Major(string value) => ParseVersion(value)?.FirstOrDefault() ?? -1;

        private static string? MaxVersion(IEnumerable<string> versions)
        {
            string? best = null;
            foreach (string value in versions)
            {
                if (ParseVersion(value) is null) continue;
                if (best is null || CompareVersionValues(value, best) > 0) best = value;
            }
            return best;
        }

        /// <summary>
        /// ASUS uses both ordinary four-part versions and five-part component bundle
        /// versions (for example Dolby 9.710.553.21.7). System.Version rejects the
        /// latter, so comparisons use unbounded numeric segments.
        /// </summary>
        private static int[]? ParseVersion(string value)
        {
            string[] parts = value.Trim().TrimStart('V', 'v').Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var result = new int[parts.Length];
            for (int index = 0; index < parts.Length; index++)
            {
                string digits = new string(parts[index].TakeWhile(char.IsDigit).ToArray());
                if (digits.Length == 0 || !int.TryParse(digits, out result[index])) return null;
            }
            return result;
        }

        private static int CompareVersionValues(string left, string right)
        {
            int[]? a = ParseVersion(left);
            int[]? b = ParseVersion(right);
            if (a is null || b is null) return 0;
            for (int index = 0; index < Math.Max(a.Length, b.Length); index++)
            {
                int av = index < a.Length ? a[index] : 0;
                int bv = index < b.Length ? b[index] : 0;
                if (av != bv) return av.CompareTo(bv);
            }
            return 0;
        }

        private static string CleanupDeviceId(string input)
        {
            int revision = input.IndexOf("&REV_", StringComparison.OrdinalIgnoreCase);
            return revision < 0 ? input : input[..revision];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey { public Guid fmtid; public uint pid; }

        private static readonly DevPropKey DeviceDescription = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };
        private static readonly DevPropKey DeviceHardwareIds = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 3 };
        private static readonly DevPropKey DeviceDriverVersion = new() { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 3 };
        private static readonly DevPropKey DeviceExtendedConfigurationIds = new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 15 };

        private const uint PresentDevices = 0x00000100;
        private const int BufferSmall = 0x1A;

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, uint bufferLength, uint flags);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("CfgMgr32.dll")]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, in DevPropKey key, out uint propertyType, byte[]? buffer, ref uint bufferSize, uint flags);

        private static string[] GetPresentDeviceIds()
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, null, PresentDevices) != 0 || length == 0)
                return Array.Empty<string>();
            var buffer = new char[length];
            if (CM_Get_Device_ID_ListW(null, buffer, length, PresentDevices) != 0)
                return Array.Empty<string>();
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private static byte[]? GetDevNodeProperty(uint devInst, DevPropKey key)
        {
            var buffer = new byte[2048];
            uint size = (uint)buffer.Length;
            int result = CM_Get_DevNode_PropertyW(devInst, key, out _, buffer, ref size, 0);
            if (result == BufferSmall)
            {
                buffer = new byte[size];
                result = CM_Get_DevNode_PropertyW(devInst, key, out _, buffer, ref size, 0);
            }
            if (result != 0 || size == 0) return null;
            return size == buffer.Length ? buffer : buffer[..(int)size];
        }

        private static string? PropertyString(byte[]? buffer) =>
            buffer is null ? null : Encoding.Unicode.GetString(buffer).TrimEnd('\0');

        private static string[] PropertyList(byte[]? buffer) =>
            buffer is null ? Array.Empty<string>() : Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
