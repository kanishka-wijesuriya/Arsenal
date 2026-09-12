using System;
using System.Collections.Generic;
using System.Linq;

namespace Arsenal.Peripherals.Keyboard
{
    /// <summary>What a drawn element is, which decides how the preview paints it.</summary>
    public enum LaptopKeyKind
    {
        Key,

        /// <summary>A segment of the front-edge light bar, where the machine has one.</summary>
        Lightbar,
    }

    /// <summary>The row above the function keys, which differs by product line.</summary>
    public enum LaptopHotkeyStyle
    {
        /// <summary>No such row: TUF and the consumer lines.</summary>
        None,

        /// <summary>Four keys marked M1 to M4, set in from the left edge. Zephyrus.</summary>
        MacroKeys,

        /// <summary>
        /// Five keys starting at the left edge - volume down and up, microphone mute,
        /// fan profile and the Armoury key. This is the row the driver's own LED map
        /// names VDN VUP MICM HPFN ARMC.
        /// </summary>
        MediaKeys,
    }

    /// <summary>How the arrow keys are fitted in, which changes two rows.</summary>
    public enum LaptopArrowStyle
    {
        /// <summary>
        /// Full-height arrows: up is tucked in beside a shortened right shift, and
        /// left, down and right sit on the bottom row.
        /// </summary>
        Tucked,

        /// <summary>
        /// A cluster in the bottom row with up and down stacked at half height between
        /// full-height left and right. Right shift keeps its full width.
        /// </summary>
        HalfHeightCluster,
    }

    /// <summary>
    /// One key, placed in key units: 1.0 is the width of an ordinary letter key.
    /// <see cref="Zone"/> is the backlight zone it belongs to on four-zone hardware,
    /// and <see cref="CentreX"/>/<see cref="CentreY"/> are normalised 0-1 across the
    /// whole keyboard, which is what the travelling effects are sampled with.
    /// </summary>
    public sealed record LaptopKey(
        string Label,
        double X,
        double Y,
        double Width,
        double Height,
        int Zone,
        double CentreX,
        double CentreY,
        LaptopKeyKind Kind = LaptopKeyKind.Key);

    public sealed record LaptopKeyboardModel(
        IReadOnlyList<LaptopKey> Keys,
        double Width,
        double Height,
        bool HasNumpad,
        bool HasLightbar);

    /// <summary>
    /// How the laptop's own keyboard is arranged.
    ///
    /// The region and the light bar come from the hardware, the arrangement from
    /// <see cref="LaptopKeyboardCatalog"/>, and the number pad can be overridden by
    /// the user - nothing on the machine reports whether it has one.
    /// </summary>
    public readonly record struct LaptopKeyboardOptions(
        bool Numpad = false,
        bool Iso = false,
        bool Lightbar = false,
        LaptopHotkeyStyle Hotkeys = LaptopHotkeyStyle.MediaKeys,
        LaptopArrowStyle Arrows = LaptopArrowStyle.Tucked,

        /// <summary>A power button sitting at the right of the hotkey row.</summary>
        bool PowerButton = false,

        /// <summary>
        /// The Copilot key that arrived on 2024 machines, which takes the place of the
        /// right control key rather than being added beside it.
        /// </summary>
        bool CopilotKey = false);

    /// <summary>
    /// The built-in keyboard of an ASUS ROG or TUF laptop, as a set of placed keys.
    ///
    /// This is not the same thing as <see cref="AuraKeyboardLayouts"/>, which describes
    /// external ASUS keyboards and exists to address their per-key LEDs over the wire.
    /// This one exists to be drawn.
    ///
    /// The rows follow the LED map in <c>Aura.packetMap</c> - the same map the driver
    /// writes colours through - so a key drawn here is a key with a light behind it.
    /// That map is a superset covering several chassis, which is why the arrangement
    /// itself comes from <see cref="LaptopKeyboardCatalog"/>.
    /// </summary>
    public static class LaptopKeyboardLayout
    {
        /// <summary>Width of the main block, in key units. Every row is built to it.</summary>
        public const double MainWidth = 15.0;

        private const double NumpadGap = 0.25;
        private const double NumpadWidth = 4.0;

        private const double HotkeyRowHeight = 0.62;
        private const double FunctionRowHeight = 0.82;
        private const double RowGap = 0.06;
        private const double LightbarHeight = 0.28;

        /// <summary>A row entry: a label, its width, and any gap before it.</summary>
        private readonly record struct Slot(string Label, double Width = 1, double Gap = 0);

        /// <summary>
        /// Four vertical bands across the main block, which is exactly how the
        /// driver's own zone tables divide the keyboard: keys fall into zone 0 to 3
        /// left to right, and anything past the main block belongs to the rightmost
        /// zone with the number pad.
        /// </summary>
        private static int ZoneAt(double centreX)
        {
            if (centreX >= MainWidth) return 3;
            return Math.Clamp((int)(centreX / (MainWidth / 4)), 0, 3);
        }

        /// <summary>
        /// The keyboard this machine has, as far as anything can tell.
        ///
        /// The region comes from the keyboard's own factory layout code and the light
        /// bar from its feature bits; the arrangement comes from
        /// <see cref="LaptopKeyboardCatalog"/>, because nothing on the machine reports
        /// it. See that class for why it is a table.
        /// </summary>
        public static LaptopKeyboardMatch Detect() => LaptopKeyboardCatalog.Resolve(
            LaptopKeyboardCatalog.ModelKey(),
            iso: AuraKeyboardLayouts.IsIsoLayoutCode(USB.Aura.KeyboardLayoutId),
            lightbar: USB.Aura.HasLightbar);

        public static LaptopKeyboardModel Build(LaptopKeyboardOptions options)
        {
            var keys = new List<LaptopKey>();
            double y = 0;

            if (options.Hotkeys != LaptopHotkeyStyle.None)
            {
                AddHotkeyRow(keys, y, options);
                y += HotkeyRowHeight + RowGap;
            }

            // Escape stands apart from the function keys on a laptop, and the row ends
            // with Delete rather than the print-screen cluster of a desktop board.
            AddRow(keys, y, FunctionRowHeight, scaleToWidth: true, new Slot[]
            {
                new("Esc"),
                new("F1", Gap: 0.4), new("F2"), new("F3"), new("F4"), new("F5"), new("F6"),
                new("F7"), new("F8"), new("F9"), new("F10"), new("F11"), new("F12"),
                new("Del"),
            });
            y += FunctionRowHeight + RowGap;

            double numberRowY = y;
            AddRow(keys, y, 1, scaleToWidth: false, new Slot[]
            {
                new("`"), new("1"), new("2"), new("3"), new("4"), new("5"), new("6"),
                new("7"), new("8"), new("9"), new("0"), new("-"), new("="), new("Bksp", 2),
            });
            y += 1 + RowGap;

            double tabRowY = y;
            AddRow(keys, y, 1, scaleToWidth: false, options.Iso
                ? new Slot[]
                {
                    new("Tab", 1.5), new("Q"), new("W"), new("E"), new("R"), new("T"), new("Y"),
                    new("U"), new("I"), new("O"), new("P"), new("["), new("]", 1.25),
                }
                : new Slot[]
                {
                    new("Tab", 1.5), new("Q"), new("W"), new("E"), new("R"), new("T"), new("Y"),
                    new("U"), new("I"), new("O"), new("P"), new("["), new("]"), new("\\", 1.5),
                });
            y += 1 + RowGap;

            if (options.Iso)
            {
                AddRow(keys, y, 1, scaleToWidth: false, new Slot[]
                {
                    new("Caps", 1.75), new("A"), new("S"), new("D"), new("F"), new("G"), new("H"),
                    new("J"), new("K"), new("L"), new(";"), new("'"), new("#"),
                });

                // Placed by hand because it spans the two rows either side of it.
                AddKey(keys, "Enter", MainWidth - 1.25, tabRowY, 1.25, 2 + RowGap);
            }
            else
            {
                AddRow(keys, y, 1, scaleToWidth: false, new Slot[]
                {
                    new("Caps", 1.75), new("A"), new("S"), new("D"), new("F"), new("G"), new("H"),
                    new("J"), new("K"), new("L"), new(";"), new("'"), new("Enter", 2.25),
                });
            }
            y += 1 + RowGap;

            AddShiftRow(keys, y, options);
            y += 1 + RowGap;

            double bottomRowY = y;
            AddBottomRow(keys, y, options);
            y += 1;

            double width = MainWidth;
            if (options.Numpad)
            {
                width = MainWidth + NumpadGap + NumpadWidth;
                AddNumpad(keys, numberRowY, tabRowY, bottomRowY);
            }

            double height = y;

            if (options.Lightbar)
            {
                height += RowGap * 3;
                AddLightbar(keys, height, width);
                height += LightbarHeight;
            }

            // The keys are inset by a margin so the glow that bleeds past the outermost
            // ones has somewhere to land instead of being clipped by the control's edge.
            const double margin = 0.16;
            double totalWidth = width + margin * 2;
            double totalHeight = height + margin * 2;

            List<LaptopKey> placed = keys
                .Select(key => key with
                {
                    X = key.X + margin,
                    Y = key.Y + margin,
                    CentreX = (key.X + margin + key.Width / 2) / totalWidth,
                    CentreY = (key.Y + margin + key.Height / 2) / totalHeight,
                })
                .ToList();

            return new LaptopKeyboardModel(placed, totalWidth, totalHeight, options.Numpad, options.Lightbar);
        }

        /// <summary>
        /// The row above the function keys, plus the power button that sits at its
        /// right on the chassis that put one there.
        /// </summary>
        private static void AddHotkeyRow(List<LaptopKey> keys, double y, LaptopKeyboardOptions options)
        {
            Slot[] row = options.Hotkeys == LaptopHotkeyStyle.MacroKeys
                // Set in from the left edge, which is where they sit on a Zephyrus.
                ? new Slot[] { new("M1", 1, 1.6), new("M2"), new("M3"), new("M4") }
                : new Slot[] { new("Vol−"), new("Vol+"), new("Mic"), new("Fan"), new("ROG") };

            AddRow(keys, y, HotkeyRowHeight, scaleToWidth: false, row);

            if (options.PowerButton) AddKey(keys, "⏻", MainWidth - 1.1, y, 1.1, HotkeyRowHeight);
        }

        /// <summary>
        /// The shift row. A tucked arrow cluster steals width from the right shift for
        /// the up key; a half-height cluster leaves it at full width.
        /// </summary>
        private static void AddShiftRow(List<LaptopKey> keys, double y, LaptopKeyboardOptions options)
        {
            bool tucked = options.Arrows == LaptopArrowStyle.Tucked;

            var row = new List<Slot>();
            if (options.Iso)
            {
                row.Add(new("Shift", 1.25));
                row.Add(new("<"));
            }
            else
            {
                row.Add(new("Shift", 2.25));
            }

            foreach (string letter in new[] { "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/" })
                row.Add(new(letter));

            row.Add(new("Shift", tucked ? 1.75 : 2.75));
            if (tucked) row.Add(new("↑"));

            AddRow(keys, y, 1, scaleToWidth: false, row.ToArray());
        }

        /// <summary>
        /// The bottom row, which ends either in three full-height arrows or in a
        /// cluster with up and down stacked at half height.
        /// </summary>
        private static void AddBottomRow(List<LaptopKey> keys, double y, LaptopKeyboardOptions options)
        {
            // The Copilot key took the right control key's place rather than being
            // added beside it, so the row is the same width either way.
            string rightModifier = options.CopilotKey ? "Copilot" : "Ctrl";

            var row = new List<Slot>
            {
                new("Ctrl", 1.25), new("Fn"), new("Win"), new("Alt", 1.25),
                new("Space", 5.5), new("Alt"), new(rightModifier),
            };

            if (options.Arrows == LaptopArrowStyle.Tucked)
            {
                row.Add(new("←"));
                row.Add(new("↓"));
                row.Add(new("→"));
                AddRow(keys, y, 1, scaleToWidth: false, row.ToArray());
                return;
            }

            AddRow(keys, y, 1, scaleToWidth: false, row.ToArray());

            // Left and right are full height; up and down share the middle column.
            double clusterX = MainWidth - 3;
            double half = (1 - RowGap) / 2;
            AddKey(keys, "←", clusterX, y, 1, 1);
            AddKey(keys, "↑", clusterX + 1, y, 1, half);
            AddKey(keys, "↓", clusterX + 1, y + half + RowGap, 1, half);
            AddKey(keys, "→", clusterX + 2, y, 1, 1);
        }

        /// <summary>
        /// The number pad, in the shape the driver's LED map describes: four wide, with
        /// a double-height plus and Enter and a double-width zero.
        /// </summary>
        private static void AddNumpad(List<LaptopKey> keys, double numberRowY, double tabRowY, double bottomRowY)
        {
            double x = MainWidth + NumpadGap;
            double rowStep = 1 + RowGap;

            AddRow(keys, numberRowY, 1, scaleToWidth: false, new Slot[]
            {
                new("Num"), new("/"), new("*"), new("−"),
            }, startX: x);

            AddRow(keys, tabRowY, 1, scaleToWidth: false, new Slot[] { new("7"), new("8"), new("9") }, startX: x);
            AddKey(keys, "+", x + 3, tabRowY, 1, 2 + RowGap);

            AddRow(keys, tabRowY + rowStep, 1, scaleToWidth: false, new Slot[] { new("4"), new("5"), new("6") }, startX: x);
            AddRow(keys, tabRowY + rowStep * 2, 1, scaleToWidth: false, new Slot[] { new("1"), new("2"), new("3") }, startX: x);
            AddKey(keys, "Enter", x + 3, tabRowY + rowStep * 2, 1, 2 + RowGap);

            AddRow(keys, bottomRowY, 1, scaleToWidth: false, new Slot[] { new("0", 2), new(".") }, startX: x);
        }

        /// <summary>
        /// Six segments along the front edge. Their zones are the four the driver
        /// reserves for the bar, two segments each, outermost first.
        /// </summary>
        private static void AddLightbar(List<LaptopKey> keys, double y, double width)
        {
            int[] zones = { 5, 5, 4, 6, 7, 7 };
            double segment = width / zones.Length;

            for (int i = 0; i < zones.Length; i++)
            {
                keys.Add(new LaptopKey(
                    string.Empty, i * segment, y, segment - 0.06, LightbarHeight,
                    zones[i], 0, 0, LaptopKeyKind.Lightbar));
            }
        }

        private static void AddRow(
            List<LaptopKey> keys, double y, double height, bool scaleToWidth,
            Slot[] slots, double startX = 0)
        {
            double total = slots.Sum(slot => slot.Width + slot.Gap);
            double scale = scaleToWidth && total > 0 ? MainWidth / total : 1;

            double x = startX;
            foreach (Slot slot in slots)
            {
                x += slot.Gap * scale;
                double width = slot.Width * scale;
                AddKey(keys, slot.Label, x, y, width, height);
                x += width;
            }
        }

        private static void AddKey(List<LaptopKey> keys, string label, double x, double y, double width, double height)
        {
            keys.Add(new LaptopKey(label, x, y, width, height, ZoneAt(x + width / 2), 0, 0));
        }
    }
}
