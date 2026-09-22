using System;

namespace Arsenal.AnimeMatrix
{
    /// <summary>
    /// Works out how bright each segment of the Slash bar is at a moment in time, for
    /// the on-screen preview of the lid.
    ///
    /// This is a portrayal, not a readback. Every firmware mode is played by the bar's
    /// own controller once <see cref="SlashDevice.SetMode"/> has named it, and the
    /// device reports nothing about where in the animation it is:
    /// <see cref="SlashDevice.GetRecord"/> reads settings records and the selected
    /// mode, never the live frame. So the preview reproduces each effect's shape and
    /// pace. It matches what the lid is doing; it is not in step with it, and cannot be.
    ///
    /// The shapes are written as functions of normalised position so one definition
    /// serves both bar lengths: seven segments on a GA403 or GU605, thirty five on the
    /// long chassis, with no second set of tables to keep in agreement.
    ///
    /// Battery is the exception and is exact, because Arsenal computes that one itself:
    /// <see cref="Percentage"/> below is the same code the device is sent.
    /// </summary>
    public static class SlashEffectSimulator
    {
        /// <summary>
        /// How long one frame lasts. The bar steps between whole frames rather than
        /// sliding, so the preview quantises time the same way: sampling continuously
        /// would give a smoothness that seven LEDs do not have.
        ///
        /// The figure is chosen, not recovered. ASUS keeps its own constant inlined in
        /// a native image, and its effect files do not pin it down either, running
        /// anywhere from 47ms to 125ms per frame against their paired audio.
        /// </summary>
        public const int StepMilliseconds = 90;

        /// <summary>Full brightness, and the two shades the effects lead and trail with.</summary>
        private const byte Full = 255;
        private const byte Lead = 190;
        private const byte Trail = 120;

        /// <summary>True when the mode moves, rather than holding one state.</summary>
        public static bool IsAnimated(SlashMode mode) => mode switch
        {
            SlashMode.Static or SlashMode.Dark or SlashMode.BatteryLevel => false,
            _ => true,
        };

        /// <summary>
        /// The battery gauge: a run of lit segments filling from the far end, with a
        /// part lit segment at the boundary.
        /// </summary>
        /// <param name="length">Segments on this chassis, 7 or 35.</param>
        /// <param name="brightness">0 to 3, the level the Slash brightness keys set.</param>
        public static byte[] Percentage(int length, int brightness, double percentage)
        {
            double step = 100.0 / length;
            int bracket = (int)Math.Floor(percentage / step);
            if (bracket >= length) return Filled(length, (byte)(brightness * 85.333));

            byte[] pattern = new byte[length];
            for (int i = length - 1; i > length - 1 - bracket; i--)
                pattern[i] = (byte)(brightness * 85.333);

            pattern[length - 1 - bracket] = (byte)(((percentage % step) * brightness * 85.333) / step);
            return pattern;
        }

        /// <summary>
        /// Fills <paramref name="frame"/> with the brightness of each segment at
        /// <paramref name="step"/>, counted in <see cref="StepMilliseconds"/> from any
        /// fixed origin. Values are the effect at full brightness; dimming is left to
        /// the caller, so that changing brightness does not restart the motion.
        /// </summary>
        public static void Sample(SlashMode mode, int step, byte[] frame)
        {
            int n = frame.Length;
            if (n == 0) return;
            Array.Clear(frame, 0, n);
            if (step < 0) step = 0;

            // The authored table when this machine has one, which is the difference
            // between matching the lid and resembling it. The shapes below are the
            // fallback, not the first choice.
            if (TryAuthored(mode, step, frame)) return;

            switch (mode)
            {
                case SlashMode.Static:
                    Fill(frame, Full);
                    break;

                case SlashMode.Dark:
                    break;

                // A head running end to end and turning round, with two segments of
                // wake behind it. The wake is what makes the direction readable at
                // seven segments; without it the bar only looks like it is blinking.
                case SlashMode.Bounce:
                {
                    int span = Math.Max(1, n - 1);
                    int phase = step % (span * 2);
                    bool forward = phase <= span;
                    int head = forward ? phase : span * 2 - phase;
                    Comet(frame, head, forward ? 1 : -1);
                    break;
                }

                // One direction only, running off the end before it starts again. The
                // gap is what tells Slash apart from Bounce.
                case SlashMode.Slash:
                {
                    int head = step % (n + 3);
                    Comet(frame, head, 1);
                    break;
                }

                // Fills, holds full for a beat, blanks, repeats.
                case SlashMode.Loading:
                {
                    int phase = step % (n + 3);
                    for (int i = 0; i < n && i < phase; i++) frame[i] = Full;
                    if (phase == n) Fill(frame, Full);
                    break;
                }

                // Lit at both ends, collapsing inwards to meet in the middle, then a
                // rest before it starts again: the leading pair full, the pair behind
                // at Lead, the pair behind those at Trail.
                case SlashMode.Flow:
                {
                    int half = (n + 1) / 2;
                    int phase = step % (half + 4);
                    if (phase < half)
                    {
                        Wave(frame, phase, Full);
                        Wave(frame, phase - 1, Lead);
                        Wave(frame, phase - 2, Trail);
                    }
                    break;
                }

                // Several pulses in flight at once, travelling the length of the bar.
                case SlashMode.Transmission:
                {
                    for (int i = 0; i < n; i++)
                    {
                        int d = Mod(i - step, 4);
                        frame[i] = d switch { 0 => Full, 1 => Lead, 2 => Trail, _ => (byte)0 };
                    }
                    break;
                }

                // Segments flicking on and off out of step with each other. Hashed on
                // index and step so the scatter is the same on every run, which matters
                // when two windows show the same mode side by side.
                case SlashMode.BitStream:
                {
                    for (int i = 0; i < n; i++)
                    {
                        uint h = Hash(i, step);
                        if ((h & 3) == 0) frame[i] = Full;
                        else if ((h & 7) == 1) frame[i] = Trail;
                    }
                    break;
                }

                // The whole bar breathing, the far end a little behind the near end so
                // the swell reads as travelling rather than pulsing flat.
                case SlashMode.Phantom:
                {
                    for (int i = 0; i < n; i++)
                    {
                        double u = Position(i, n);
                        frame[i] = Level(0.5 - 0.5 * Math.Cos(step * 0.16 - u * 1.4));
                    }
                    break;
                }

                // A sine running the length of the bar. Every segment is lit to some
                // degree, which is what separates it from the comet effects.
                case SlashMode.Flux:
                {
                    for (int i = 0; i < n; i++)
                    {
                        double u = Position(i, n);
                        double v = 0.5 + 0.5 * Math.Sin(u * 6.3 - step * 0.45);
                        frame[i] = Level(v * v);
                    }
                    break;
                }

                // Each segment rising and falling on its own, the way a spectrum
                // display does. Three sines of unrelated periods, so it never settles
                // into a pattern the eye can follow.
                case SlashMode.Spectrum:
                {
                    double t = step * 0.3;
                    for (int i = 0; i < n; i++)
                    {
                        double u = Position(i, n);
                        double v = Math.Sin(t + u * 5.1)
                                 + Math.Sin(t * 0.73 + u * 11.3)
                                 + Math.Sin(t * 1.37 + u * 2.7);
                        frame[i] = Level(Math.Clamp(v / 3.0 + 0.45, 0, 1));
                    }
                    break;
                }

                // Halves alternating, held long enough to read as a warning rather
                // than a flicker.
                case SlashMode.Hazard:
                {
                    bool near = (step / 2) % 2 == 0;
                    for (int i = 0; i < n; i++)
                        if (i < n / 2 == near) frame[i] = Full;
                    break;
                }

                // Two heads closing on the middle, meeting, holding, then opening out
                // again to the ends.
                case SlashMode.Interfacing:
                {
                    int half = (n + 1) / 2;
                    int cycle = half * 2 + 2;
                    int phase = step % cycle;
                    int reach = phase < half ? phase
                              : phase < half + 2 ? half - 1
                              : cycle - phase;
                    for (int i = 0; i <= reach && i < n; i++)
                    {
                        frame[i] = Full;
                        frame[n - 1 - i] = Full;
                    }
                    break;
                }

                // A fill that climbs, slips back, and climbs again. The stumble is the
                // whole character of it on the real bar.
                case SlashMode.Ramp:
                {
                    int cycle = n * 2;
                    int phase = step % cycle;
                    int reach = phase <= n ? phase : cycle - phase;
                    if (reach > 0 && (Hash(0, step) & 3) == 0) reach--;
                    for (int i = 0; i < reach && i < n; i++) frame[n - 1 - i] = Full;
                    if (reach < n) frame[n - 1 - reach] = Lead;
                    break;
                }

                // Lit, then a stutter as it dies, then dark for long enough to land.
                case SlashMode.GameOver:
                {
                    int phase = step % 14;
                    if (phase < 4) Fill(frame, Full);
                    else if (phase < 9 && phase % 2 == 0) Fill(frame, Trail);
                    break;
                }

                // A sweep in, then two flashes of the whole bar.
                case SlashMode.Start:
                {
                    int phase = step % (n + 7);
                    if (phase < n)
                    {
                        for (int i = 0; i <= phase && i < n; i++) frame[i] = i == phase ? Full : Lead;
                    }
                    else if (phase == n || phase == n + 2)
                    {
                        Fill(frame, Full);
                    }
                    break;
                }

                // Two short and one long, the shape of an alert.
                case SlashMode.Buzzer:
                {
                    int phase = step % 12;
                    if (phase == 0 || phase == 2 || (phase >= 5 && phase <= 8)) Fill(frame, Full);
                    break;
                }

                // The three FX slots are glitch effects on the lid. FX1 scatters,
                // FX2 throws a short block around, FX3 strobes.
                case SlashMode.FX1:
                {
                    if ((step & 3) == 2) break;
                    for (int i = 0; i < n; i++)
                        if ((Hash(i, step / 2) & 1) == 0) frame[i] = Full;
                    break;
                }

                case SlashMode.FX2:
                {
                    if (step % 3 == 2) break;
                    int width = Math.Max(1, n / 3);
                    int start = (int)(Hash(1, step / 3) % (uint)Math.Max(1, n - width + 1));
                    for (int i = start; i < start + width && i < n; i++) frame[i] = Full;
                    break;
                }

                case SlashMode.FX3:
                    if (step % 3 != 2) Fill(frame, Full);
                    break;

                // Arsenal computes these itself. Battery is exact. The audio meter is a
                // stand in, because the preview is not wired to the capture that the
                // lighting timer reads.
                case SlashMode.BatteryLevel:
                    Array.Copy(Percentage(n, 3, BatteryPercentage()), frame, n);
                    break;

                case SlashMode.Audio:
                case SlashMode.AudioSpectrum:
                {
                    double t = step * 0.34;
                    double bass = 0.5 + 0.5 * Math.Sin(t);
                    double treble = 0.5 + 0.5 * Math.Sin(t * 1.9 + 1.1);
                    for (int i = 0; i < n; i++)
                    {
                        double u = Position(i, n);
                        double v = Math.Max(bass - u, 0) + Math.Max(treble - (1 - u), 0);
                        frame[n - 1 - i] = Level(Math.Clamp(v, 0, 1));
                    }
                    break;
                }

                default:
                    Fill(frame, Full);
                    break;
            }
        }

        /// <summary>
        /// Plays the frame ASUS authored for this mode, if the machine carries one.
        ///
        /// The tables are written for the bar they shipped with, which is the bar this
        /// is drawing, so the widths normally agree. They are stretched rather than
        /// refused when they do not, because a table written seven wide still describes
        /// the same effect on a bar of thirty five: each authored value covers a run of
        /// segments, which is how the long bar is grouped anyway.
        /// </summary>
        private static bool TryAuthored(SlashMode mode, int step, byte[] frame)
        {
            if (!SlashContentLibrary.TryGetFrames(mode, out byte[][] frames)) return false;

            byte[] row = frames[step % frames.Length];
            int n = frame.Length;

            if (row.Length == n)
            {
                Array.Copy(row, frame, n);
                return true;
            }

            for (int i = 0; i < n; i++)
                frame[i] = row[Math.Min(row.Length - 1, i * row.Length / n)];

            return true;
        }

        /// <summary>A head at <paramref name="head"/> with two segments of wake behind it.</summary>
        private static void Comet(byte[] frame, int head, int direction)
        {
            Put(frame, head, Full);
            Put(frame, head - direction, Lead);
            Put(frame, head - direction * 2, Trail);
        }

        /// <summary>Lights the pair of segments <paramref name="offset"/> in from each end.</summary>
        private static void Wave(byte[] frame, int offset, byte value)
        {
            if (offset < 0) return;
            Put(frame, offset, value);
            Put(frame, frame.Length - 1 - offset, value);
        }

        private static void Put(byte[] frame, int index, byte value)
        {
            if (index < 0 || index >= frame.Length) return;
            if (frame[index] < value) frame[index] = value;
        }

        private static void Fill(byte[] frame, byte value)
        {
            for (int i = 0; i < frame.Length; i++) frame[i] = value;
        }

        private static byte[] Filled(int length, byte value)
        {
            byte[] frame = new byte[length];
            Fill(frame, value);
            return frame;
        }

        /// <summary>Position along the bar, 0 at the hinge end and 1 at the far end.</summary>
        private static double Position(int index, int length)
            => length <= 1 ? 0 : (double)index / (length - 1);

        private static byte Level(double v) => (byte)Math.Clamp(v * 255.0, 0, 255);

        private static int Mod(int value, int m) => ((value % m) + m) % m;

        /// <summary>
        /// A stable scatter. The effects that look random on the lid have to look the
        /// same on every run, or the preview would disagree with itself between two
        /// windows showing the same mode.
        /// </summary>
        private static uint Hash(int index, int step)
        {
            unchecked
            {
                uint h = (uint)(index * 374761393 + step * 668265263);
                h = (h ^ (h >> 13)) * 1274126177;
                return h ^ (h >> 16);
            }
        }

        private static double BatteryPercentage()
        {
            try
            {
                return 100 * (HardwareControl.GetBatteryChargePercentage() / AppConfig.Get("charge_limit", 100));
            }
            catch
            {
                return 100;
            }
        }
    }
}
