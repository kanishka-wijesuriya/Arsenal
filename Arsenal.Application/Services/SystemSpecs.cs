using Arsenal.Helpers;
using Microsoft.Win32;
using System.Management;
using System.Text;

namespace Arsenal.Application.Services
{
    /// <summary>
    /// What Arsenal could read about this machine. Any field the machine would not
    /// answer for is null, and the About page hides that row rather than showing a
    /// labelled blank.
    /// </summary>
    public sealed record SystemSpecSheet(
        string? Model,
        string? Bios,
        string? Processor,
        string? Memory,
        string? Graphics,
        string? Display,
        string? Storage,
        string? OperatingSystem);

    /// <summary>
    /// Reads the machine's specification for the About page.
    /// </summary>
    /// <remarks>
    /// Everything here is best-effort and individually guarded. A machine that will not
    /// answer one query still shows every other line, because a specification list that
    /// disappears entirely because one WMI provider is unhappy is worse than one with a
    /// gap in it. Gathered once, off the UI thread, and cached for the process.
    /// </remarks>
    public static class SystemSpecs
    {
        private static SystemSpecSheet? _cached;
        private static readonly object Gate = new();

        public static SystemSpecSheet Get()
        {
            lock (Gate)
            {
                return _cached ??= Gather();
            }
        }

        private static SystemSpecSheet Gather() => new(
            Read("Model", () => AppConfig.GetModel()),
            Read("BIOS", () => AppConfig.GetBiosAndModel().Item1),
            Read("Processor", ReadProcessor),
            Read("Memory", ReadMemory),
            Read("Graphics", ReadGraphics),
            Read("Display", ReadDisplay),
            Read("Storage", ReadStorage),
            Read("Operating system", ReadWindows));

        /// <summary>Runs one reader, returning null rather than throwing.</summary>
        private static string? Read(string label, Func<string?> read)
        {
            try
            {
                string? value = read();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Specs [{label}]: {ex.Message}");
                return null;
            }
        }

        private static string? ReadProcessor()
        {
            // The registry answers instantly; the equivalent WMI class is famously slow
            // because querying it re-runs the processor validation on some machines.
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

            string? name = key?.GetValue("ProcessorNameString") as string;
            if (string.IsNullOrWhiteSpace(name)) return null;

            name = CollapseSpaces(name);
            int threads = Environment.ProcessorCount;

            return $"{name} · {threads} threads";
        }

        private static string? ReadMemory()
        {
            ulong bytes = 0;
            var speeds = new List<uint>();

            using (var searcher = new ManagementObjectSearcher(
                "SELECT Capacity, Speed FROM Win32_PhysicalMemory"))
            {
                foreach (ManagementObject stick in searcher.Get())
                {
                    using (stick)
                    {
                        if (stick["Capacity"] is not null) bytes += Convert.ToUInt64(stick["Capacity"]);
                        if (stick["Speed"] is not null) speeds.Add(Convert.ToUInt32(stick["Speed"]));
                    }
                }
            }

            if (bytes == 0) return null;

            double gigabytes = bytes / 1024d / 1024d / 1024d;
            string text = $"{Math.Round(gigabytes)} GB";

            if (speeds.Count > 0) text += $" · {speeds.Max()} MT/s";
            return text;
        }

        private static string? ReadGraphics()
        {
            var adapters = new List<string>();

            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM FROM Win32_VideoController"))
            {
                foreach (ManagementObject adapter in searcher.Get())
                {
                    using (adapter)
                    {
                        if (adapter["Name"] is string name && !string.IsNullOrWhiteSpace(name))
                            adapters.Add(CollapseSpaces(name));
                    }
                }
            }

            // Duplicates are common when a display is attached through a dock.
            return adapters.Count == 0 ? null : string.Join("  ·  ", adapters.Distinct());
        }

        private static string? ReadDisplay()
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen is null) return null;

            var text = new StringBuilder($"{screen.Bounds.Width} × {screen.Bounds.Height}");

            // The internal panel specifically, not whichever monitor Windows calls primary.
            string? laptopScreen = Display.ScreenNative.FindLaptopScreen(true);
            int refresh = Display.ScreenNative.GetRefreshRate(laptopScreen);
            int maximum = Display.ScreenControl.GetMaxRate(laptopScreen);

            if (refresh > 0) text.Append($" · {refresh} Hz");
            if (maximum > refresh) text.Append($" (up to {maximum} Hz)");

            if (AppConfig.IsOLED()) text.Append(" · OLED");
            else if (AppConfig.IsForceMiniled()) text.Append(" · Mini-LED");

            return text.ToString();
        }

        private static string? ReadStorage()
        {
            var drives = new List<string>();

            using (var searcher = new ManagementObjectSearcher(
                "SELECT Model, Size, MediaType FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject disk in searcher.Get())
                {
                    using (disk)
                    {
                        if (disk["Size"] is null) continue;

                        double gigabytes = Convert.ToUInt64(disk["Size"]) / 1000d / 1000d / 1000d;
                        if (gigabytes < 1) continue;

                        string model = disk["Model"] as string ?? "Drive";
                        drives.Add($"{CollapseSpaces(model)} ({Math.Round(gigabytes)} GB)");
                    }
                }
            }

            return drives.Count == 0 ? null : string.Join("  ·  ", drives);
        }

        private static string? ReadWindows()
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return null;

            string product = key.GetValue("ProductName") as string ?? "Windows";
            string display = key.GetValue("DisplayVersion") as string ?? string.Empty;
            string build = key.GetValue("CurrentBuild") as string ?? string.Empty;
            string revision = key.GetValue("UBR") is int ubr ? "." + ubr : string.Empty;

            // The registry still says "Windows 10" on Windows 11; the build number is
            // what actually distinguishes them.
            if (int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
                product = product.Replace("Windows 10", "Windows 11");

            var text = new StringBuilder(product);
            if (display.Length > 0) text.Append($" {display}");
            if (build.Length > 0) text.Append($" (build {build}{revision})");

            return text.ToString();
        }

        private static string CollapseSpaces(string value)
            => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
