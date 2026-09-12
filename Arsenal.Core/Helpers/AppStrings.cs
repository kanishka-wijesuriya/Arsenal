using System.Globalization;

namespace Arsenal.Helpers
{
    /// <summary>
    /// Display text, looked up by key against the shared string resources. The
    /// generated <see cref="Properties.Strings"/> class is internal to this assembly,
    /// so the UI reaches the same resources through here.
    /// </summary>
    public static class AppStrings
    {
        /// <summary>
        /// The text for <paramref name="key"/> in the current UI culture. A key whose
        /// translation is still missing falls back to the neutral English resource,
        /// and a key with no resource at all falls back to the key itself so a new
        /// string is visible on screen rather than blank.
        /// </summary>
        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            try
            {
                return Properties.Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Missing resource '{key}': {ex.Message}");
                return key;
            }
        }

        /// <summary>
        /// The text for <paramref name="key"/> with <paramref name="arguments"/>
        /// substituted into its placeholders.
        /// </summary>
        public static string Format(string key, params object[] arguments)
        {
            string text = Get(key);
            try
            {
                return string.Format(CultureInfo.CurrentCulture, text, arguments);
            }
            catch (FormatException)
            {
                // A translation whose placeholders were mangled must not take the UI
                // down; show the unsubstituted text instead.
                return text;
            }
        }
    }
}
