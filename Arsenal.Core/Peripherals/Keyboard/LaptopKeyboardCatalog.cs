using Arsenal.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Arsenal.Peripherals.Keyboard
{
    /// <summary>How well the drawn keyboard is known to match the machine.</summary>
    public enum LaptopKeyboardConfidence
    {
        /// <summary>Nothing matched; the shape is a generic ROG laptop.</summary>
        Generic,

        /// <summary>The chassis family matched, which is what decides the arrangement.</summary>
        Chassis,

        /// <summary>The user set the shape by hand, which beats everything else.</summary>
        UserSet,
    }

    public readonly record struct LaptopKeyboardMatch(
        LaptopKeyboardOptions Options,
        string ChassisName,
        LaptopKeyboardConfidence Confidence);

    /// <summary>
    /// Which keyboard each ASUS laptop chassis has.
    ///
    /// This is a hand-written table and it is worth being plain about why. Nothing on
    /// the machine reports the physical arrangement of its own keys. The Aura probe
    /// gives the backlight zoning, the region code and the light bar; ASUS's own
    /// registry record gives the SKU; neither says whether there is a number pad or a
    /// row of hotkeys above the function keys. Armoury Crate ships no layout data
    /// either - only product renders, and only when ROG Live Service downloaded them.
    ///
    /// So the arrangement is looked up by chassis, the way the rest of AppConfig
    /// already decides what a model can do. Entries describe a chassis family rather
    /// than a single SKU, because within a family the keyboard is the same part: every
    /// Strix 17 has the number pad, no Zephyrus 16 does. A model that matches nothing
    /// falls back to a generic ROG shape, and the switches on the Lighting page
    /// override the lot - a correction the user makes is remembered and always wins.
    /// </summary>
    public static class LaptopKeyboardCatalog
    {
        /// <summary>
        /// One chassis family: the model prefixes it covers and the keyboard it has.
        ///
        /// Prefixes are matched against the model number ASUS reports, so "G713"
        /// covers G713RM, G713PV and the rest of that chassis.
        /// </summary>
        private sealed record Chassis(
            string Name,
            string[] Prefixes,
            bool Numpad,
            LaptopHotkeyStyle Hotkeys,
            LaptopArrowStyle Arrows,
            bool PowerButton = false,
            bool CopilotKey = false);

        /// <summary>
        /// Ordered longest-prefix-first at lookup, so a chassis whose number is a
        /// prefix of another cannot swallow it.
        ///
        /// The Zephyrus G16 entry is the one verified against a real machine: a
        /// photograph of a GU605MI, which is where the M1-M4 row, the power button,
        /// the half-height arrow cluster and the Copilot key in the right control key's
        /// place all come from. The rest are authored from chassis-family knowledge and
        /// should be treated as a starting point until someone checks them.
        /// </summary>
        private static readonly Chassis[] Families =
        {
            // Zephyrus: thin chassis, no number pad at any size, four macro keys set in
            // from the left, and a power button at the right of that row.
            new("ROG Zephyrus G14", new[] { "GA401", "GA402", "GA403" },
                Numpad: false, Hotkeys: LaptopHotkeyStyle.MacroKeys, Arrows: LaptopArrowStyle.HalfHeightCluster,
                PowerButton: true),

            new("ROG Zephyrus G16", new[] { "GU603", "GU604", "GU605", "GA605" },
                Numpad: false, Hotkeys: LaptopHotkeyStyle.MacroKeys, Arrows: LaptopArrowStyle.HalfHeightCluster,
                PowerButton: true, CopilotKey: true),

            new("ROG Zephyrus Duo", new[] { "GX550", "GX551", "GX650" },
                Numpad: true, Hotkeys: LaptopHotkeyStyle.MacroKeys, Arrows: LaptopArrowStyle.HalfHeightCluster,
                PowerButton: true),

            // Strix: light bar and the five-key media row throughout; the number pad
            // arrives with the 17 and 18 inch chassis.
            new("ROG Strix 15/16", new[] { "G512", "G513", "G533", "G614", "G615", "G634" },
                Numpad: false, Hotkeys: LaptopHotkeyStyle.MediaKeys, Arrows: LaptopArrowStyle.Tucked),

            new("ROG Strix 17/18", new[] { "G712", "G713", "G733", "G814", "G834", "G815" },
                Numpad: true, Hotkeys: LaptopHotkeyStyle.MediaKeys, Arrows: LaptopArrowStyle.Tucked),

            // Flow: no hotkey row on the small convertibles.
            new("ROG Flow X13", new[] { "GV301", "GV302" },
                Numpad: false, Hotkeys: LaptopHotkeyStyle.None, Arrows: LaptopArrowStyle.HalfHeightCluster),

            new("ROG Flow Z13", new[] { "GZ301", "GZ302" },
                Numpad: false, Hotkeys: LaptopHotkeyStyle.None, Arrows: LaptopArrowStyle.HalfHeightCluster),

            new("ROG Flow X16", new[] { "GV601" },
                Numpad: false, Hotkeys: LaptopHotkeyStyle.MediaKeys, Arrows: LaptopArrowStyle.Tucked),

            // TUF: number pad at both sizes, and no row above the function keys.
            new("TUF Gaming 15", new[] { "FA506", "FX506", "FA507", "FX507", "FA508", "FX508" },
                Numpad: true, Hotkeys: LaptopHotkeyStyle.None, Arrows: LaptopArrowStyle.Tucked),

            new("TUF Gaming 17", new[] { "FA706", "FX706", "FA707", "FX707" },
                Numpad: true, Hotkeys: LaptopHotkeyStyle.None, Arrows: LaptopArrowStyle.Tucked),
        };

        /// <summary>
        /// Works out the keyboard for this machine.
        ///
        /// The hardware supplies what it can - the region code from the keyboard's own
        /// factory layout byte and the light bar from its feature bits - and the
        /// chassis table supplies the arrangement, which nothing on the machine
        /// reports.
        /// </summary>
        /// <param name="model">
        /// The model number, normally <c>AppConfig.GetModelShort()</c>. ASUS's own
        /// registry record is preferred when it has one, since it is the exact SKU
        /// rather than a marketing name.
        /// </param>
        public static LaptopKeyboardMatch Resolve(string model, bool iso, bool lightbar)
        {
            string key = Normalise(model);

            Chassis? match = Families
                .SelectMany(family => family.Prefixes.Select(prefix => (family, prefix)))
                .Where(pair => key.Contains(pair.prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(pair => pair.prefix.Length)
                .Select(pair => pair.family)
                .FirstOrDefault();

            if (match is null)
            {
                // A generic ROG laptop: no number pad, and the hotkey row only where
                // the machine is a ROG at all, since TUF and the consumer lines have
                // no such row.
                return new LaptopKeyboardMatch(
                    new LaptopKeyboardOptions(
                        Numpad: false, Iso: iso, Lightbar: lightbar,
                        Hotkeys: AppConfig.IsROG() && !AppConfig.IsTUF()
                            ? LaptopHotkeyStyle.MediaKeys
                            : LaptopHotkeyStyle.None),
                    string.Empty,
                    LaptopKeyboardConfidence.Generic);
            }

            return new LaptopKeyboardMatch(
                new LaptopKeyboardOptions(
                    Numpad: match.Numpad, Iso: iso, Lightbar: lightbar,
                    Hotkeys: match.Hotkeys, Arrows: match.Arrows,
                    PowerButton: match.PowerButton, CopilotKey: match.CopilotKey),
                match.Name,
                LaptopKeyboardConfidence.Chassis);
        }

        /// <summary>
        /// The best model string available: ASUS's own SKU record where it exists,
        /// and the DMI model number otherwise.
        /// </summary>
        public static string ModelKey()
        {
            string series = AsusMachineInfo.Series;
            return series.Length > 0 ? series : AppConfig.GetModelShort();
        }

        private static string Normalise(string model)
            => new(model.Where(character => !char.IsWhiteSpace(character)).ToArray());
    }
}
