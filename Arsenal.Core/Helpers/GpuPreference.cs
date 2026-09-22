using Microsoft.Win32;

namespace Arsenal.Helpers
{
    /// <summary>
    /// Asks Windows to draw Arsenal on the integrated graphics.
    /// </summary>
    /// <remarks>
    /// This is the same per-application preference the Graphics settings page writes, and
    /// the same one Armoury Crate sets for its own executables. Windows reads it when the
    /// process creates its first Direct3D device and hands out the adapter accordingly.
    ///
    /// <para>A control panel has no business on the discrete card. Rendering a settings
    /// window there wakes a GPU that draws tens of watts, keeps it awake for as long as a
    /// window is open, and loads that vendor's shader compiler and D3D driver - on this
    /// class of machine, over 130MB of libraries - for a job the integrated adapter does
    /// without noticing. It is also the difference between Arsenal appearing in the
    /// battery report as an application that used the discrete GPU and not appearing at
    /// all.</para>
    ///
    /// <para><b>Keyed on the executable's full path</b>, which is the part worth knowing:
    /// a preference set for one build does not carry to a build in a different folder, so
    /// it is written on every start rather than once at install. That is also why the
    /// stale entries are swept - a machine that has run a dozen builds from a dozen
    /// folders otherwise collects a dozen dead registry values.</para>
    ///
    /// <para>It applies from the next start. By the time a window exists the adapter has
    /// already been chosen, so writing it changes nothing for the process doing the
    /// writing.</para>
    /// </remarks>
    public static class GpuPreference
    {
        private const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

        /// <summary>Windows' value for "power saving", which is the integrated adapter.</summary>
        private const string PowerSaving = "GpuPreference=1;";

        public static void PreferIntegrated()
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path)) return;

                using RegistryKey preferences = Registry.CurrentUser.CreateSubKey(Key, writable: true);
                if (preferences is null) return;

                if (preferences.GetValue(path) as string != PowerSaving)
                {
                    preferences.SetValue(path, PowerSaving, RegistryValueKind.String);
                    Logger.WriteLine("Asked Windows to draw Arsenal on the integrated graphics; it applies from the next start.");
                }

                SweepMovedBuilds(preferences, path);
            }
            catch (Exception ex)
            {
                // A machine policy can refuse this key. Nothing else depends on it.
                Logger.WriteLine("GPU preference: " + ex.Message);
            }
        }

        /// <summary>
        /// Removes preferences left behind by copies of Arsenal that are no longer there.
        /// </summary>
        /// <remarks>
        /// Only entries that name an Arsenal executable, and only where that executable
        /// has actually gone. Every other application's preferences are none of ours, and
        /// a path that merely is not reachable right now - a removable drive, a network
        /// share - is not the same as one that has been deleted.
        /// </remarks>
        private static void SweepMovedBuilds(RegistryKey preferences, string current)
        {
            foreach (string name in preferences.GetValueNames())
            {
                if (name.Length == 0 || string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(@"\Arsenal.exe", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    string? root = Path.GetPathRoot(name);
                    if (root is null || !Directory.Exists(root)) continue;
                    if (File.Exists(name)) continue;

                    preferences.DeleteValue(name, throwOnMissingValue: false);
                    Logger.WriteLine("Cleared the graphics preference for a build that is no longer there: " + name);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("GPU preference sweep: " + ex.Message);
                }
            }
        }
    }
}
