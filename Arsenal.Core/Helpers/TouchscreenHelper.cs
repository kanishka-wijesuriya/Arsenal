using Arsenal.Helpers;
using System.Management;

public static class TouchscreenHelper
{
    private static readonly Lazy<bool> HasTouchscreen = new(DetectAvailability,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Uses the same PnP friendly-name identity as the legacy app, but distinguishes an
    /// absent device from a present device that happens to be disabled.
    /// </summary>
    public static bool IsAvailable() => HasTouchscreen.Value;

    private static bool DetectAvailability()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%touch%screen%'");
            using ManagementObjectCollection devices = searcher.Get();
            return devices.Count > 0;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Can't detect touchscreen: {ex.Message}");
            return false;
        }
    }

    public static bool? GetStatus()
    {
        try
        {
            if (!IsAvailable()) return null;
            ProcessHelper.RunAsAdmin();
            return ProcessHelper.RunCMD("powershell", "(Get-PnpDevice -FriendlyName '*touch*screen*').Status").Contains("OK");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Can't get touchscreen status: {ex.Message}");
            return null;
        }
    }

    public static void ToggleTouchscreen(bool status)
    {
        try
        {
            ProcessHelper.RunAsAdmin();
            ProcessHelper.RunCMD("powershell", (status ? "Enable-PnpDevice" : "Disable-PnpDevice") + " -InstanceId (Get-PnpDevice -FriendlyName '*touch*screen*').InstanceId -Confirm:$false");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Can't toggle touchscreen: {ex.Message}");
        }

    }
}
