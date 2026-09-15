using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Arsenal.Display
{

    public static class ScreenNative
    {
        public const int ENUM_CURRENT_SETTINGS = -1;
        public const string DefaultDevice = @"\\.\DISPLAY1";

        /// <summary>
        /// Returns true if at least one active display is not the built-in internal panel.
        /// </summary>
        public static bool IsExternalDisplayConnected(bool log = false)
        {
            try
            {
                foreach (var device in GetAllDevices())
                {
                    if (!IsInternalDisplay(device))
                    {
                        if (log) Logger.WriteLine("Found external screen: " + device.monitorFriendlyDeviceName + ":" + device.outputTechnology);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.ToString());
            }

            return false;
        }

        private static bool IsInternalDisplay(DisplayNative.DISPLAYCONFIG_TARGET_DEVICE_NAME device)
        {
            if (device.outputTechnology == DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                || device.outputTechnology == DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED)
                return true;

            // Plenty of built-in panels report no friendly name at all - this machine's
            // SDC41A3 is one - so the remembered name can be empty. Matching on an empty
            // string would file every nameless external monitor as the internal panel.
            string? internalName = AppConfig.GetString("internal_display");
            return !string.IsNullOrEmpty(internalName)
                && device.monitorFriendlyDeviceName == internalName;
        }

        /// <summary>
        /// Returns all active display target device names.
        /// </summary>
        public static IEnumerable<DisplayNative.DISPLAYCONFIG_TARGET_DEVICE_NAME> GetAllDevices()
        {
            var err = DisplayNative.GetDisplayConfigBufferSizes(
                DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
            if (err != 0) throw new Win32Exception(err);

            var paths = new DisplayNative.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DisplayNative.DISPLAYCONFIG_MODE_INFO[modeCount];
            err = DisplayNative.QueryDisplayConfig(
                DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                ref pathCount, paths, ref modeCount, modes, nint.Zero);
            if (err != 0) throw new Win32Exception(err);

            for (int i = 0; i < modeCount; i++)
            {
                if (modes[i].infoType != DisplayNative.DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
                    continue;

                var deviceName = new DisplayNative.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                deviceName.header.type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                deviceName.header.size = (uint)Marshal.SizeOf(deviceName);
                deviceName.header.adapterId = modes[i].adapterId;
                deviceName.header.id = modes[i].id;

                err = DisplayNative.DisplayConfigGetDeviceInfo(ref deviceName);
                if (err == 0)
                    yield return deviceName;
                else
                    Logger.WriteLine("DisplayConfigGetDeviceInfo error: " + new Win32Exception(err).Message);
            }
        }

        /// <summary>
        /// Finds the GDI device name of the internal laptop screen (e.g. \\.\DISPLAY1).
        /// Optimized: single QueryDisplayConfig pass resolves the GDI name via
        /// DISPLAYCONFIG_SOURCE_DEVICE_NAME, eliminating the EnumDisplayDevices loop.
        /// </summary>
        public static string? FindLaptopScreen(bool log = false)
        {
            try
            {
                var err = DisplayNative.GetDisplayConfigBufferSizes(
                    DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (err != 0) throw new Win32Exception(err);

                var paths = new DisplayNative.DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DisplayNative.DISPLAYCONFIG_MODE_INFO[modeCount];
                err = DisplayNative.QueryDisplayConfig(
                    DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount, paths, ref modeCount, modes, nint.Zero);
                if (err != 0) throw new Win32Exception(err);

                foreach (var path in paths)
                {
                    var targetName = new DisplayNative.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    targetName.header.type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    targetName.header.size = (uint)Marshal.SizeOf(targetName);
                    targetName.header.adapterId = path.targetInfo.adapterId;
                    targetName.header.id = path.targetInfo.id;

                    if (DisplayNative.DisplayConfigGetDeviceInfo(ref targetName) != 0) continue;
                    if (!IsInternalDisplay(targetName)) continue;

                    if (log) Logger.WriteLine(targetName.monitorDevicePath + " " + targetName.outputTechnology);
                    // Only remember a name that can identify anything later. Writing the
                    // empty string a nameless panel reports turns the fallback match in
                    // IsInternalDisplay into a match on every nameless monitor.
                    if (!string.IsNullOrEmpty(targetName.monitorFriendlyDeviceName))
                        AppConfig.Set("internal_display", targetName.monitorFriendlyDeviceName);

                    // Resolve GDI device name directly from the source path entry � no EnumDisplayDevices needed
                    var sourceName = new DisplayNative.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    sourceName.header.type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    sourceName.header.size = (uint)Marshal.SizeOf(sourceName);
                    sourceName.header.adapterId = path.sourceInfo.adapterId;
                    sourceName.header.id = path.sourceInfo.id;

                    if (DisplayNative.DisplayConfigGetDeviceInfo(ref sourceName) == 0)
                        return ExtractDisplay(sourceName.viewGdiDeviceName);

                    return Screen.PrimaryScreen?.DeviceName;
                }

                if (log) Logger.WriteLine("Internal screen off");
                return null;
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The panel's highest mode. A null device name means the internal panel is not
        /// currently enumerable - clamshell, external-only, or a topology still settling
        /// after a hotplug - which says nothing about what the panel can do, so the last
        /// real reading answers instead of the -1 that used to leak out to the UI.
        /// </summary>
        public static int GetMaxRefreshRate(string? laptopScreen)
        {
            int frequency = -1;

            if (laptopScreen is not null)
            {
                var dm = CreateDevmode();
                int i = 0;
                while (DisplayNative.EnumDisplaySettingsEx(laptopScreen, i, ref dm) != 0)
                {
                    if (dm.dmDisplayFrequency > frequency) frequency = dm.dmDisplayFrequency;
                    i++;
                }
            }

            if (frequency > 0) AppConfig.Set("screen_max", frequency);
            else frequency = AppConfig.Get("screen_max");

            return frequency;
        }

        /// <summary>
        /// The highest mode a GDI device offers, with no caching. The cache in
        /// <see cref="GetMaxRefreshRate"/> belongs to the internal panel alone; an
        /// external monitor's ceiling must never be written into it.
        /// </summary>
        private static int EnumerateMaxRefreshRate(string device, int width, int height)
        {
            var dm = CreateDevmode();
            int frequency = -1;
            int i = 0;
            while (DisplayNative.EnumDisplaySettingsEx(device, i, ref dm) != 0)
            {
                // Only modes at the resolution the display is actually running. A
                // monitor's highest rate often belongs to a lower resolution, and
                // "3440 × 1440 · up to 240 Hz" would be a rate it cannot reach there.
                i++;
                if (dm.dmPelsWidth != width || dm.dmPelsHeight != height) continue;
                if (dm.dmDisplayFrequency > frequency) frequency = dm.dmDisplayFrequency;
            }
            return frequency;
        }

        /// <summary>
        /// Every display Windows is currently driving, internal panel included, with the
        /// mode each one is actually running. This is a report, not a control surface:
        /// the app writes refresh rate and overdrive to the ASUS panel only.
        /// </summary>
        public static List<ActiveDisplayInfo> GetActiveDisplays()
        {
            var displays = new List<ActiveDisplayInfo>();

            try
            {
                var err = DisplayNative.GetDisplayConfigBufferSizes(
                    DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (err != 0) throw new Win32Exception(err);

                var paths = new DisplayNative.DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DisplayNative.DISPLAYCONFIG_MODE_INFO[modeCount];
                err = DisplayNative.QueryDisplayConfig(
                    DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount, paths, ref modeCount, modes, nint.Zero);
                if (err != 0) throw new Win32Exception(err);

                string? primaryDevice = Screen.PrimaryScreen?.DeviceName;

                for (int i = 0; i < pathCount; i++)
                {
                    var path = paths[i];

                    var targetName = new DisplayNative.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    targetName.header.type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    targetName.header.size = (uint)Marshal.SizeOf(targetName);
                    targetName.header.adapterId = path.targetInfo.adapterId;
                    targetName.header.id = path.targetInfo.id;
                    if (DisplayNative.DisplayConfigGetDeviceInfo(ref targetName) != 0) continue;

                    var sourceName = new DisplayNative.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    sourceName.header.type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    sourceName.header.size = (uint)Marshal.SizeOf(sourceName);
                    sourceName.header.adapterId = path.sourceInfo.adapterId;
                    sourceName.header.id = path.sourceInfo.id;
                    if (DisplayNative.DisplayConfigGetDeviceInfo(ref sourceName) != 0) continue;

                    string gdiName = ExtractDisplay(sourceName.viewGdiDeviceName);

                    var dm = CreateDevmode();
                    if (DisplayNative.EnumDisplaySettingsEx(gdiName, ENUM_CURRENT_SETTINGS, ref dm) == 0) continue;

                    bool isInternal = IsInternalDisplay(targetName);

                    displays.Add(new ActiveDisplayInfo(
                        Name: DescribeDisplay(targetName, isInternal),
                        GdiDeviceName: gdiName,
                        RefreshRate: dm.dmDisplayFrequency,
                        MaxRefreshRate: Math.Max(dm.dmDisplayFrequency, EnumerateMaxRefreshRate(gdiName, dm.dmPelsWidth, dm.dmPelsHeight)),
                        Width: dm.dmPelsWidth,
                        Height: dm.dmPelsHeight,
                        IsInternal: isInternal,
                        IsPrimary: gdiName == primaryDevice));
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("GetActiveDisplays: " + ex.Message);
            }

            // Mirrored paths share one source, so the same GDI device can appear twice.
            return displays
                .GroupBy(display => display.GdiDeviceName)
                .Select(group => group.First())
                .OrderByDescending(display => display.IsInternal)
                .ToList();
        }

        /// <summary>
        /// A label for a display that has one. Many built-in panels report an empty
        /// friendly name, and the raw device path is not something to put in the UI.
        /// </summary>
        private static string DescribeDisplay(DisplayNative.DISPLAYCONFIG_TARGET_DEVICE_NAME device, bool isInternal)
        {
            string friendly = device.monitorFriendlyDeviceName?.Trim() ?? string.Empty;
            if (friendly.Length > 0) return friendly;
            return isInternal ? "Built-in display" : "External display";
        }

        public static int GetRefreshRate(string? laptopScreen)
        {
            if (laptopScreen is null) return -1;

            var dm = CreateDevmode();
            return DisplayNative.EnumDisplaySettingsEx(laptopScreen, ENUM_CURRENT_SETTINGS, ref dm) != 0
                ? dm.dmDisplayFrequency
                : -1;
        }

        public static int SetRefreshRate(string laptopScreen, int frequency = 120)
        {
            var dm = CreateDevmode();
            if (DisplayNative.EnumDisplaySettingsEx(laptopScreen, ENUM_CURRENT_SETTINGS, ref dm) == 0) return 0;
            if (dm.dmDisplayFrequency == frequency) return 0;

            dm.dmDisplayFrequency = frequency;
            int result = DisplayNative.ChangeDisplaySettingsEx(
                laptopScreen, ref dm, IntPtr.Zero, DisplayNative.DisplaySettingsFlags.CDS_UPDATEREGISTRY, IntPtr.Zero);
            Logger.WriteLine("Screen = " + frequency + "Hz : " + (result == 0 ? "OK" : result));
            return result;
        }

        public static DisplayNative.DEVMODE CreateDevmode()
        {
            var dm = new DisplayNative.DEVMODE
            {
                dmDeviceName = new string(new char[32]),
                dmFormName = new string(new char[32]),
            };
            dm.dmSize = (short)Marshal.SizeOf(dm);
            return dm;
        }

        private static string ExtractDisplay(string input)
        {
            // Find the first backslash after the UNC prefix (\\.\)
            int index = input.IndexOf((char)92, 4);
            return index != -1 ? input.Substring(0, index) : input;
        }
    }

    /// <summary>One display Windows is currently driving, and the mode it is running.</summary>
    public record ActiveDisplayInfo(
        string Name,
        string GdiDeviceName,
        int RefreshRate,
        int MaxRefreshRate,
        int Width,
        int Height,
        bool IsInternal,
        bool IsPrimary);
}
