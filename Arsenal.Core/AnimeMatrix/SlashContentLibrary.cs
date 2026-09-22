using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arsenal.AnimeMatrix
{
    /// <summary>
    /// The frame tables for the Slash bar's firmware effects, as shipped for this
    /// machine.
    ///
    /// The effects are played by the bar's own controller and it reports nothing about
    /// where in one it is, so a preview either reproduces the shape or reads the same
    /// tables the firmware was built from. ASUS ships those tables with the machine
    /// rather than with Armoury Crate: ROG Live Service downloads a chassis folder of
    /// <c>.slashlighting</c> files, each a small INI naming a mode byte and listing one
    /// brightness per segment per frame. Those are the exact frames, so reading them is
    /// the only way the preview can match the lid rather than resemble it.
    ///
    /// Read at runtime and never bundled. They belong to the machine, and Arsenal is
    /// not entitled to redistribute them. A copy is kept in Arsenal's own folder for
    /// the same reason the device render is: removing Armoury Crate takes the source
    /// files away, and the tables are still correct for a chassis that has not changed.
    /// Where there is nothing to read, <see cref="SlashEffectSimulator"/> draws its own
    /// shape instead.
    /// </summary>
    public static class SlashContentLibrary
    {
        private const string ContentRoot = @"ASUS\ROG Live Service\SlashContent";

        /// <summary>
        /// A folder name shorter than this is not a chassis family. The content root
        /// holds one folder per family, named like the model without its SKU suffix.
        /// </summary>
        private const int MinimumFamilyLength = 4;

        private static readonly Lazy<Library> _library = new(Load);

        private sealed record Library(string Origin, IReadOnlyDictionary<byte, byte[][]> Frames);

        /// <summary>
        /// The authored frames for <paramref name="mode"/>, or false when this machine
        /// has no table for it. Each row is one frame, one byte per segment.
        /// </summary>
        public static bool TryGetFrames(SlashMode mode, out byte[][] frames)
        {
            frames = Array.Empty<byte[]>();

            byte code = SlashDevice.GetModeCode(mode);
            if (code == 0) return false;

            if (!_library.Value.Frames.TryGetValue(code, out byte[][]? found) || found.Length == 0) return false;

            frames = found;
            return true;
        }

        /// <summary>Where the tables came from, for the log and the About page.</summary>
        public static string Origin => _library.Value.Origin;

        /// <summary>Modes this machine has a table for.</summary>
        public static int Count => _library.Value.Frames.Count;

        private static Library Load()
        {
            try
            {
                string? folder = FindContentFolder();

                if (folder is null)
                {
                    Library? cached = ReadCache();
                    if (cached is not null) return cached;

                    Logger.WriteLine("Slash frames: none shipped for this machine, using drawn shapes");
                    return new Library(string.Empty, new Dictionary<byte, byte[][]>());
                }

                Dictionary<byte, byte[][]> frames = ReadFolder(folder);
                string origin = Path.GetFileName(folder);

                if (frames.Count == 0) return ReadCache() ?? new Library(string.Empty, frames);

                WriteCache(origin, frames);
                Logger.WriteLine($"Slash frames: {frames.Count} effects read for {origin}");
                return new Library(origin, frames);
            }
            catch (Exception exception)
            {
                Logger.WriteLine("Slash frames unavailable: " + exception.Message);
                return new Library(string.Empty, new Dictionary<byte, byte[][]>());
            }
        }

        /// <summary>
        /// The chassis folder for this machine. The folders are named by family, so
        /// "GU605" serves a GU605MI; the longest name the model starts with wins, so a
        /// family that is a prefix of another cannot claim it.
        /// </summary>
        private static string? FindContentFolder()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ContentRoot);

            if (!Directory.Exists(root)) return null;

            string model = AppConfig.GetModelShort().ToUpperInvariant();
            if (model.Length == 0) return null;

            return Directory.EnumerateDirectories(root)
                .Where(path => Path.GetFileName(path).Length >= MinimumFamilyLength)
                .Where(path => model.StartsWith(Path.GetFileName(path).ToUpperInvariant(), StringComparison.Ordinal))
                .OrderByDescending(path => Path.GetFileName(path).Length)
                .FirstOrDefault();
        }

        private static Dictionary<byte, byte[][]> ReadFolder(string folder)
        {
            var frames = new Dictionary<byte, byte[][]>();
            string content = Path.Combine(folder, "Content");
            if (!Directory.Exists(content)) return frames;

            foreach (string file in Directory.EnumerateFiles(content, "*.slashlighting", SearchOption.AllDirectories))
            {
                if (!TryParse(File.ReadAllLines(file), out byte code, out byte[][] parsed)) continue;

                // Several themes each carry a Static entry under the same mode byte.
                // The first is as good as the last, and keeping one keeps the table
                // honest about how many distinct effects there are.
                if (!frames.ContainsKey(code)) frames[code] = parsed;
            }

            return frames;
        }

        /// <summary>
        /// The file is an INI with a UUID section naming the mode byte and a FRAMES
        /// section of <c>index=v,v,v,</c> rows, one brightness per segment and a
        /// trailing comma.
        /// </summary>
        private static bool TryParse(IEnumerable<string> lines, out byte code, out byte[][] frames)
        {
            code = 0;
            var rows = new List<byte[]>();
            bool inFrames = false;
            int width = 0;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith('['))
                {
                    inFrames = line.Equals("[FRAMES]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0) continue;

                string key = line[..split].Trim();
                string value = line[(split + 1)..].Trim();

                if (!inFrames)
                {
                    if (key.Equals("UUID", StringComparison.OrdinalIgnoreCase)
                        && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        && byte.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out byte parsedCode))
                    {
                        code = parsedCode;
                    }

                    continue;
                }

                byte[] row = value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part => byte.TryParse(part, out byte level) ? level : (byte)0)
                    .ToArray();

                if (row.Length == 0) continue;
                if (width == 0) width = row.Length;
                if (row.Length != width) continue;

                rows.Add(row);
            }

            frames = rows.ToArray();
            return code != 0 && frames.Length > 0;
        }

        // -----------------------------------------------------------------
        // The copy kept for when Armoury Crate is no longer installed
        // -----------------------------------------------------------------

        private static string CacheFile => Path.Combine(Logger.appPath, "slash-frames.txt");

        private static void WriteCache(string origin, Dictionary<byte, byte[][]> frames)
        {
            try
            {
                var lines = new List<string> { "origin=" + origin };

                foreach ((byte code, byte[][] rows) in frames.OrderBy(pair => pair.Key))
                {
                    lines.Add("mode=" + code.ToString("X2"));
                    lines.AddRange(rows.Select(row => string.Join(',', row)));
                }

                File.WriteAllLines(CacheFile, lines);
            }
            catch (Exception exception)
            {
                Logger.WriteLine("Slash frames not cached: " + exception.Message);
            }
        }

        private static Library? ReadCache()
        {
            try
            {
                if (!File.Exists(CacheFile)) return null;

                string origin = string.Empty;
                var frames = new Dictionary<byte, byte[][]>();
                var rows = new List<byte[]>();
                byte current = 0;

                void Flush()
                {
                    if (current != 0 && rows.Count > 0) frames[current] = rows.ToArray();
                    rows.Clear();
                }

                foreach (string line in File.ReadAllLines(CacheFile))
                {
                    if (line.StartsWith("origin=", StringComparison.Ordinal))
                    {
                        origin = line[7..].Trim();
                    }
                    else if (line.StartsWith("mode=", StringComparison.Ordinal))
                    {
                        Flush();
                        current = byte.TryParse(line.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out byte code)
                            ? code : (byte)0;
                    }
                    else if (line.Length > 0)
                    {
                        rows.Add(line.Split(',').Select(part => byte.TryParse(part, out byte level) ? level : (byte)0).ToArray());
                    }
                }

                Flush();

                // A cache written on a different machine would draw the wrong bar. The
                // origin is a family name, so it has to still name this chassis.
                if (frames.Count == 0) return null;
                if (origin.Length > 0 && !AppConfig.GetModelShort().StartsWith(origin, StringComparison.OrdinalIgnoreCase)) return null;

                Logger.WriteLine($"Slash frames: source is gone, using the cached copy for {origin}");
                return new Library(origin, frames);
            }
            catch
            {
                return null;
            }
        }
    }
}
