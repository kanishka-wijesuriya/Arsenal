using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Colors = System.Windows.Media.Colors;

namespace Arsenal.UI.Services;

/// <summary>
/// Owns the single accent choice used by both Arsenal's design tokens and WPF UI.
/// An absent preference intentionally means Windows, so existing installations pick
/// up the user's Personalisation colour without a migration or a new default setting.
/// </summary>
internal static class AccentColorService
{
    public const string SourceSetting = "accent_source";
    public const string ColorSetting = "accent_color";

    private static readonly Color NeutralFallback = Color.FromRgb(0x8A, 0x88, 0x86);

    public static bool UsesWindowsAccent => AppConfig.Get(SourceSetting, 0) == 0;

    public static Color GetConfiguredAccent()
        => UsesWindowsAccent ? GetWindowsAccent() : GetCustomAccent();

    public static Color GetCustomAccent()
        => TryParse(AppConfig.GetString(ColorSetting), out Color color) ? color : GetWindowsAccent();

    public static Color GetWindowsAccent()
    {
        try
        {
            Color color = ApplicationAccentColorManager.GetColorizationColor();
            return Color.FromRgb(color.R, color.G, color.B);
        }
        catch
        {
            try
            {
                Color color = SystemParameters.WindowGlassColor;
                return Color.FromRgb(color.R, color.G, color.B);
            }
            catch
            {
                // Startup must stay usable even if DWM/WinRT colour APIs are unavailable
                // (for example in a service session). A neutral fallback avoids silently
                // restoring the blue palette the Windows-accent option replaces.
                return NeutralFallback;
            }
        }
    }

    public static bool TryParse(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            object parsed = ColorConverter.ConvertFromString(value.Trim());
            if (parsed is not Color converted) return false;
            color = Color.FromRgb(converted.R, converted.G, converted.B);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    public static Color Blend(Color background, Color foreground, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte BlendChannel(byte behind, byte above)
            => (byte)Math.Round((behind * (1 - amount)) + (above * amount));

        return Color.FromRgb(
            BlendChannel(background.R, foreground.R),
            BlendChannel(background.G, foreground.G),
            BlendChannel(background.B, foreground.B));
    }

    public static Color ContrastingText(Color background)
    {
        double backgroundLuminance = RelativeLuminance(background);
        double blackContrast = (backgroundLuminance + 0.05) / 0.05;
        double whiteContrast = 1.05 / (backgroundLuminance + 0.05);
        return blackContrast >= whiteContrast ? Colors.Black : Colors.White;
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
    }
}
