using Microsoft.Win32;
using System;

namespace Arsenal.Peripherals.Keyboard
{
    /// <summary>
    /// What ASUS's own software recorded about this machine, read from the registry
    /// key ROG Live Service writes when it identifies the laptop.
    ///
    /// <code>
    /// HKLM\SOFTWARE\ASUS\RLSinfo
    ///     Series         = GU605MI      the SKU, more precise than the DMI name
    ///     KeyboardLayout = SINGLE       how the backlight is zoned
    ///     DeliveryDate   = 2024
    /// </code>
    ///
    /// This is a second opinion, not the first one. The Aura probe asks the keyboard
    /// itself and is authoritative when it answers; this is what the machine was sold
    /// as, and it is available immediately - including before the probe has run, and
    /// on a machine whose keyboard does not answer the probe at all.
    /// </summary>
    public static class AsusMachineInfo
    {
        private const string Path = @"SOFTWARE\ASUS\RLSinfo";

        private static readonly Lazy<(string Series, string KeyboardLayout)> _info = new(Read);

        /// <summary>The SKU ASUS recorded, for example "GU605MI". Empty when absent.</summary>
        public static string Series => _info.Value.Series;

        /// <summary>
        /// The recorded backlight zoning, as an <c>AuraBacklightType</c>, or
        /// <c>Unknown</c> when ASUS recorded nothing this can be read from.
        /// </summary>
        public static USB.AuraBacklightType RecordedBacklightType => _info.Value.KeyboardLayout.ToUpperInvariant() switch
        {
            "SINGLE" => USB.AuraBacklightType.SingleZone,
            "PERKEY" or "PER_KEY" or "PERKEYRGB" => USB.AuraBacklightType.PerKey,
            "4ZONE" or "FOURZONE" or "MULTI" or "MULTIZONE" => USB.AuraBacklightType.MultiZone,
            _ => USB.AuraBacklightType.Unknown,
        };

        private static (string, string) Read()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(Path);
                if (key is null) return (string.Empty, string.Empty);

                return (
                    key.GetValue("Series")?.ToString() ?? string.Empty,
                    key.GetValue("KeyboardLayout")?.ToString() ?? string.Empty);
            }
            catch (Exception exception)
            {
                Logger.WriteLine("ASUS machine info: " + exception.Message);
                return (string.Empty, string.Empty);
            }
        }
    }
}
