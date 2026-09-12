using Arsenal.USB;
using System;
using System.Drawing;

namespace Arsenal.Peripherals.Keyboard
{
    /// <summary>
    /// Works out what colour a key is showing at a moment in time, for the on-screen
    /// preview of the keyboard backlight.
    ///
    /// This is a portrayal, not a readback. The animated Aura modes run in the
    /// keyboard's own firmware and it reports nothing about where they are, so the
    /// preview reproduces each effect's shape and timing rather than mirroring the
    /// hardware frame for frame - the two drift apart in phase, and there is no way
    /// to synchronise them.
    ///
    /// The modes Arsenal drives itself - Heatmap, Ambient, Battery, GPU Mode,
    /// Gradient and the two audio modes - are different: the application computes
    /// those colours, so the preview is fed the real ones through
    /// <see cref="Aura.ColorsApplied"/> and none of the code here is used for them.
    /// </summary>
    public static class AuraEffectSimulator
    {
        /// <summary>
        /// True when the mode's colours come from Arsenal rather than the keyboard's
        /// firmware, and the preview should show what was actually sent.
        /// </summary>
        public static bool IsDrivenByApplication(AuraMode mode) => mode switch
        {
            AuraMode.HEATMAP or AuraMode.GPUMODE or AuraMode.AMBIENT or AuraMode.BATTERY
                or AuraMode.GRADIENT or AuraMode.ZONETEST or AuraMode.AUDIO or AuraMode.AUDIOPULSE => true,
            _ => false,
        };

        /// <summary>True for effects that only mean anything on per-key hardware.</summary>
        public static bool IsPerKeyEffect(AuraMode mode) => mode switch
        {
            AuraMode.Star or AuraMode.Rain or AuraMode.Highlight or AuraMode.Laser
                or AuraMode.Ripple or AuraMode.Comet or AuraMode.Flash => true,
            _ => false,
        };

        private static double SpeedFactor(AuraSpeed speed) => speed switch
        {
            AuraSpeed.Slow => 0.6,
            AuraSpeed.Fast => 1.9,
            _ => 1.0,
        };

        /// <summary>
        /// The colour of one key at <paramref name="time"/> seconds.
        /// </summary>
        /// <param name="x">Normalised position across the keyboard, 0 at the left.</param>
        /// <param name="y">Normalised position down the keyboard, 0 at the top.</param>
        /// <param name="index">
        /// The key's index in the layout, used to give each key a stable but unrelated
        /// phase in the scattered effects. Neighbouring keys must not twinkle together.
        /// </param>
        public static Color Sample(
            AuraMode mode, double time, double x, double y,
            Color color1, Color color2, AuraSpeed speed, int index)
        {
            double rate = SpeedFactor(speed);

            switch (mode)
            {
                case AuraMode.AuraStatic:
                    return color1;

                case AuraMode.AuraBreathe:
                {
                    // Two half-cycles: the first breathes the primary colour up and
                    // down, the second does the same with the secondary one. A black
                    // secondary colour means the firmware breathes one colour only.
                    double phase = Frac(time * 0.32 * rate);
                    bool second = phase >= 0.5 && !IsBlack(color2);
                    double local = (phase % 0.5) * 2;
                    return Scale(second ? color2 : color1, Math.Sin(local * Math.PI));
                }

                case AuraMode.AuraColorCycle:
                    return FromHsv(Frac(time * 0.12 * rate), 1, 1);

                case AuraMode.AuraRainbow:
                    // A hue gradient lying across the keyboard, travelling left to right.
                    return FromHsv(Frac(x - time * 0.18 * rate), 1, 1);

                case AuraMode.AuraStrobe:
                {
                    double phase = Frac(time * 1.1 * rate);
                    return Scale(color1, phase < 0.5 ? 1 : 0.05);
                }

                case AuraMode.Star:
                {
                    // Each key twinkles on its own schedule, from a hash of its index
                    // so the pattern is scattered but identical frame to frame.
                    double offset = Hash(index);
                    double phase = Frac(time * 0.45 * rate + offset);
                    double brightness = phase < 0.25 ? Math.Sin(phase * 4 * Math.PI) : 0;
                    return Scale(color1, Math.Max(0.04, brightness));
                }

                case AuraMode.Rain:
                {
                    // Columns falling at slightly different rates, with a short tail.
                    double column = Hash(index * 7 + 3);
                    double head = Frac(time * 0.5 * rate + column);
                    double distance = y - head;
                    if (distance < 0) distance += 1;
                    double brightness = distance < 0.28 ? 1 - distance / 0.28 : 0;
                    return Scale(color1, Math.Max(0.04, brightness));
                }

                case AuraMode.Highlight:
                {
                    // Keys light where they are pressed. Nothing is being pressed in a
                    // preview, so a slow scatter stands in for typing.
                    double phase = Frac(time * 0.3 * rate + Hash(index * 13 + 1));
                    double brightness = phase < 0.12 ? 1 - phase / 0.12 : 0;
                    return Mix(color2, color1, Math.Max(0.08, brightness));
                }

                case AuraMode.Laser:
                {
                    double head = Frac(time * 0.4 * rate);
                    double distance = Math.Abs(x - head);
                    double brightness = distance < 0.14 ? 1 - distance / 0.14 : 0;
                    return Scale(color1, Math.Max(0.04, brightness * brightness));
                }

                case AuraMode.Ripple:
                {
                    // Rings expanding from the middle of the keyboard.
                    double radius = Frac(time * 0.35 * rate);
                    double distance = Math.Sqrt(Sq(x - 0.5) * 1.6 + Sq(y - 0.5));
                    double delta = Math.Abs(distance - radius);
                    double brightness = delta < 0.12 ? 1 - delta / 0.12 : 0;
                    return Scale(color1, Math.Max(0.04, brightness));
                }

                case AuraMode.Comet:
                {
                    // A head travelling left to right with a tail trailing behind it.
                    double head = Frac(time * 0.45 * rate);
                    double distance = head - x;
                    if (distance < 0) distance += 1;
                    double brightness = distance < 0.3 ? Sq(1 - distance / 0.3) : 0;
                    return Scale(color1, Math.Max(0.04, brightness));
                }

                case AuraMode.Flash:
                {
                    // Two quick flashes, then a pause.
                    double phase = Frac(time * 0.6 * rate);
                    double brightness = phase < 0.08 || (phase >= 0.16 && phase < 0.24) ? 1 : 0.05;
                    return Scale(color1, brightness);
                }

                default:
                    return color1;
            }
        }

        private static double Frac(double value) => value - Math.Floor(value);

        private static double Sq(double value) => value * value;

        /// <summary>A cheap stable scatter in 0-1; the constants are arbitrary.</summary>
        private static double Hash(int value)
        {
            unchecked
            {
                int hash = value * 374761393 + 668265263;
                hash = (hash ^ (hash >> 13)) * 1274126177;
                return ((hash ^ (hash >> 16)) & 0x7FFFFFFF) / (double)0x7FFFFFFF;
            }
        }

        private static bool IsBlack(Color color) => color.R == 0 && color.G == 0 && color.B == 0;

        private static Color Scale(Color color, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return Color.FromArgb(
                (byte)(color.R * amount),
                (byte)(color.G * amount),
                (byte)(color.B * amount));
        }

        private static Color Mix(Color from, Color to, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return Color.FromArgb(
                (byte)(from.R + (to.R - from.R) * amount),
                (byte)(from.G + (to.G - from.G) * amount),
                (byte)(from.B + (to.B - from.B) * amount));
        }

        private static Color FromHsv(double hue, double saturation, double value)
        {
            hue = Frac(hue) * 6;
            int sector = (int)hue;
            double f = hue - sector;
            double p = value * (1 - saturation);
            double q = value * (1 - saturation * f);
            double t = value * (1 - saturation * (1 - f));

            (double r, double g, double b) = sector switch
            {
                0 => (value, t, p),
                1 => (q, value, p),
                2 => (p, value, t),
                3 => (p, q, value),
                4 => (t, p, value),
                _ => (value, p, q),
            };

            return Color.FromArgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
