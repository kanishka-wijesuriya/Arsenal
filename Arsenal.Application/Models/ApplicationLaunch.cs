namespace Arsenal.Application.Models
{
    /// <summary>Rules that decide whether an application launch should remain in the tray.</summary>
    public static class ApplicationLaunch
    {
        public const string StartMinimizedSetting = "start_minimized";

        /// <summary>
        /// Keeps ordinary launches hidden when requested without swallowing an explicit
        /// destination such as Settings, Setup, Performance or Display.
        /// </summary>
        public static bool ShouldStartMinimized(IReadOnlyList<string> arguments, bool preference)
        {
            if (arguments.Any(argument =>
                    argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("--startup", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("-m", StringComparison.OrdinalIgnoreCase)))
                return true;

            string action = arguments.FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty;
            return preference && action is "" or "--elevated";
        }
    }
}
