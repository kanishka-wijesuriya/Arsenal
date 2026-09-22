using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Arsenal.UI.Services;

/// <summary>
/// The product render for this machine - the picture of the actual laptop that
/// Armoury Crate shows on its own home screen.
///
/// ASUS ships the render with the machine rather than with the app: ROG Live Service
/// downloads a chassis-family folder holding a transparent PNG of that exact model,
/// and Armoury Crate copies it into its own package state. Reading those folders
/// means the picture is always the right one for the laptop the app happens to be
/// running on, with nothing to bundle and nothing to fetch.
///
/// The name is not guessed. The service records what it downloaded in
/// <c>GamingHostinfo.ini</c>, and Armoury Crate composes the path from two of its
/// keys - <c>SeriesName</c> for the folder, <c>ContentName</c> for the file:
///
/// <code>
/// SeriesName=GU605            ->  DeviceContent\GU605\GU605_US_0000.png
/// ContentName=GU605_US_0000
/// </code>
///
/// That matters on a chassis with several renders, one per keyboard layout, where
/// the <c>_US_</c> in the name came from the machine's own <c>NBKBLayout</c> and
/// picking any other would print the wrong key legends. Matching the model against
/// the folders on disk is kept only as a fallback for a machine whose manifest is
/// missing or names content that was never downloaded.
///
/// When nothing resolves - no Armoury Crate, or a model ASUS never shipped content
/// for - there is no render, and Home simply does without one.
/// </summary>
internal static class DeviceImageService
{
    /// <summary>
    /// Width the render is decoded down to. The shipped PNGs are square masters
    /// several thousand pixels across; Home shows the result no larger than a few
    /// hundred, so decoding at full size would cost a 60 MB intermediate for
    /// detail that is thrown away.
    /// </summary>
    private const int DecodeWidth = 480;

    /// <summary>Alpha at or below this counts as empty when trimming the margins.</summary>
    private const byte AlphaFloor = 8;

    /// <summary>
    /// A folder name shorter than this is not a chassis family - it is one of the
    /// hash-named peripheral folders that sit beside the laptop's own.
    /// </summary>
    private const int MinimumFamilyLength = 4;

    private static readonly Lazy<Task<ImageSource?>> _render = new(() => Task.Run(Load));

    /// <summary>
    /// The render for this machine, or null when ASUS shipped none. Decoding runs
    /// once per session on a background thread; the result is frozen, so callers
    /// on any thread may hold it.
    /// </summary>
    public static Task<ImageSource?> GetAsync() => _render.Value;

    private static ImageSource? Load()
    {
        try
        {
            string? source = FindRenderFile();

            // The render is a property of the laptop, not of the software that happened
            // to deliver it. Uninstalling Armoury Crate takes the source PNG with it, and
            // this used to give up there - the picture vanished from Home on a machine
            // that had not changed. Our own copy is still on disk and is still the right
            // render for this chassis, so it stands in.
            if (source is null) return ReadCacheForThisChassis();

            var file = new FileInfo(source);
            string stamp = $"{source}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";

            ImageSource? cached = ReadCache(stamp);
            if (cached is not null) return cached;

            BitmapSource render = Decode(source);
            WriteCache(render, stamp);
            return render;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Device render unavailable: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The PNG for this model, resolved the way Armoury Crate resolves it.
    /// </summary>
    private static string? FindRenderFile()
    {
        // An image the user dropped in themselves wins over anything found on disk;
        // it is the only way to get a render on a machine ASUS shipped none for.
        string supplied = Path.Combine(Logger.appPath, "device.png");
        if (File.Exists(supplied)) return supplied;

        string[] roots = ContentRoots().ToArray();

        // What Armoury Crate itself does: read the name out of the manifest.
        string? named = FindNamedRender(roots);
        if (named is not null) return named;

        // Only then fall back to searching by model, for a machine whose manifest is
        // missing or names content that was never downloaded.
        return FindRenderByModel(roots);
    }

    /// <summary>
    /// The render named by ROG Live Service's own manifest. This is Armoury Crate's
    /// route: nothing is guessed, because the service records exactly which folder and
    /// file it downloaded for this machine.
    /// </summary>
    private static string? FindNamedRender(string[] roots)
    {
        string manifest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ASUS", "ROG Live Service", "GamingHostinfo.ini");

        (string? series, string? content) = ReadHostManifest(manifest);
        if (series is null || content is null) return null;

        foreach (string root in roots)
        {
            string candidate = Path.Combine(root, series, content + ".png");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// The chassis folder and render name the manifest records for the laptop itself.
    /// The file describes every ASUS device the service knows about, one INI section
    /// each, so the laptop is the section that says it is one.
    /// </summary>
    internal static (string? Series, string? Content) ReadHostManifest(string manifest)
    {
        try
        {
            if (!File.Exists(manifest)) return (null, null);

            var section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in File.ReadLines(manifest))
            {
                string entry = line.Trim();
                if (entry.Length == 0 || entry.StartsWith(';')) continue;

                if (entry.StartsWith('[') && entry.EndsWith(']'))
                {
                    if (DescribesLaptop(section)) break;
                    section.Clear();
                    continue;
                }

                int split = entry.IndexOf('=');
                if (split > 0) section[entry[..split].Trim()] = entry[(split + 1)..].Trim();
            }

            if (!DescribesLaptop(section)) return (null, null);

            string series = section["SeriesName"];
            string content = section["ContentName"];

            // Names read out of a file are data, not a path: keep each to one segment
            // so a mangled manifest cannot point the lookup somewhere else entirely.
            if (series != Path.GetFileName(series) || content != Path.GetFileName(content))
                return (null, null);

            return (series, content);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Device render manifest: " + ex.Message);
            return (null, null);
        }
    }

    /// <summary>
    /// Whether a manifest section is the laptop's own, carrying both names needed to
    /// build the path. ASUS marks it <c>PrimitiveDeviceType=Laptop</c>.
    /// </summary>
    private static bool DescribesLaptop(Dictionary<string, string> section)
        => section.TryGetValue("PrimitiveDeviceType", out string? kind)
            && kind.Equals("Laptop", StringComparison.OrdinalIgnoreCase)
            && section.TryGetValue("SeriesName", out string? series) && series.Length > 0
            && section.TryGetValue("ContentName", out string? content) && content.Length > 0;

    /// <summary>
    /// The render found by matching the model against the folders on disk, for when
    /// the manifest cannot answer.
    /// </summary>
    private static string? FindRenderByModel(string[] roots)
    {
        // Two independent sources for the SKU, because the folders are keyed by it.
        // Win32_ComputerSystem usually carries it - "ROG Zephyrus G16 GU605MI_GU605MI" -
        // but firmware that reports only the marketing name there would leave nothing to
        // match on, and the BIOS version string names the model either way: "GU605MI.329".
        (_, string biosModel) = AppConfig.GetBiosAndModel();
        string[] candidates = new[] { AppConfig.GetModelShort(), biosModel.Trim() }
            .Where(value => value.Length >= MinimumFamilyLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string model in candidates)
        {
            foreach (string root in roots)
            {
                string? family = MatchFamilyFolder(root, model);
                if (family is null) continue;

                string? render = PickRender(family, model);
                if (render is not null) return render;
            }
        }

        return null;
    }

    private static IEnumerable<string> ContentRoots()
    {
        // Armoury Crate's own copy first: it is the one the running app draws from,
        // so matching it keeps the two applications showing the same picture.
        string packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

        string[] installs = [];
        if (Directory.Exists(packages))
        {
            try { installs = Directory.GetDirectories(packages, "*ArmouryCrate*"); }
            catch (Exception ex) { Logger.WriteLine("Device render: " + ex.Message); }
        }

        foreach (string install in installs)
        {
            string devices = Path.Combine(install, "LocalState", "Devices");
            if (Directory.Exists(devices)) yield return devices;
        }

        // The service's own download folder, which survives Armoury Crate being
        // uninstalled and is present on machines that only ever had ROG Live Service.
        string shared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ASUS", "ROG Live Service", "DeviceContent");
        if (Directory.Exists(shared)) yield return shared;
    }

    /// <summary>
    /// The folder holding this model's content. ASUS keys these by chassis family,
    /// so a GU605MI is served by the GU605 folder; the longest name that lines up
    /// with the model wins, which keeps a family folder from beating an exact one.
    /// </summary>
    private static string? MatchFamilyFolder(string root, string model)
    {
        string? best = null;

        foreach (string folder in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(folder);
            if (name.Length < MinimumFamilyLength) continue;

            if (!model.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith(model, StringComparison.OrdinalIgnoreCase)) continue;

            if (best is null || name.Length > Path.GetFileName(best).Length) best = folder;
        }

        return best;
    }

    /// <summary>
    /// The render inside a family folder. A folder can carry several - one per
    /// keyboard layout - and they only differ in the key legends, so any of them
    /// is the right laptop. Prefer a file named for the exact model, then one named
    /// for the family, and fall back to the largest, which is the full-size master
    /// rather than a thumbnail.
    /// </summary>
    private static string? PickRender(string folder, string model)
    {
        string family = Path.GetFileName(folder);
        string? best = null;
        long bestRank = long.MinValue;

        foreach (string file in Directory.GetFiles(folder, "*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Contains("preview", StringComparison.OrdinalIgnoreCase)) continue;

            long rank = new FileInfo(file).Length;
            if (name.StartsWith(model, StringComparison.OrdinalIgnoreCase)) rank += 1L << 40;
            else if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase)) rank += 1L << 39;

            if (rank <= bestRank) continue;
            bestRank = rank;
            best = file;
        }

        return best;
    }

    private static BitmapSource Decode(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        // OnLoad so the file handle is closed before this returns - the folder
        // belongs to a service that may rewrite it while Arsenal is running.
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (MeasureWidth(path) > DecodeWidth) image.DecodePixelWidth = DecodeWidth;
        image.EndInit();
        image.Freeze();

        BitmapSource trimmed = Trim(image);
        if (trimmed.CanFreeze) trimmed.Freeze();
        return trimmed;
    }

    /// <summary>
    /// The stored width, read from the PNG header alone so the size is known before
    /// deciding what to decode it at.
    /// </summary>
    private static int MeasureWidth(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return BitmapDecoder
            .Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None)
            .Frames[0].PixelWidth;
    }

    /// <summary>
    /// Drops the transparent margin. The masters are square with the laptop sitting
    /// in a band across the middle, so untrimmed they would lay out as a square with
    /// most of its height empty.
    /// </summary>
    private static BitmapSource Trim(BitmapSource source)
    {
        BitmapSource bgra = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            bgra = converted;
        }

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        int left = width, top = height, right = -1, bottom = -1;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                if (pixels[row + x * 4 + 3] <= AlphaFloor) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }

        // Nothing opaque, or nothing to cut: an opaque render is already its own crop.
        if (right < left || bottom < top) return source;
        if (left == 0 && top == 0 && right == width - 1 && bottom == height - 1) return source;

        return new CroppedBitmap(bgra, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
    }

    private static string CacheFile => Path.Combine(Logger.appPath, "device-render.png");

    private static string CacheStampFile => Path.Combine(Logger.appPath, "device-render.txt");

    /// <summary>
    /// The trimmed render kept from a previous session, when it still came from the
    /// file on disk now. Decoding the master costs a good fraction of a second, and
    /// Home is the first page shown.
    /// </summary>
    /// <summary>
    /// The cached render, used when nothing on disk can supply the original any more.
    /// </summary>
    /// <remarks>
    /// Checked against this chassis rather than returned blindly. The stamp records the
    /// path the render came from, and ASUS files each one under its chassis family -
    /// <c>...\Devices\GU605\GU605_US_0000.png</c> - so the folder name says which laptop
    /// the picture is of. A GU605MI accepts a GU605 render; a different machine reading a
    /// profile that travelled with the user does not, and gets no render rather than a
    /// picture of somebody else's laptop.
    /// </remarks>
    private static ImageSource? ReadCacheForThisChassis()
    {
        try
        {
            if (!File.Exists(CacheFile) || !File.Exists(CacheStampFile)) return null;

            string origin = File.ReadAllText(CacheStampFile).Trim().Split('|').FirstOrDefault() ?? string.Empty;
            if (!NamesThisChassis(origin)) return null;

            ImageSource cached = LoadCacheFile();
            Logger.WriteLine("Device render: source is gone, using the cached copy for " + origin);
            return cached;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Device render cache: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Whether a recorded render path belongs to the laptop this is running on.
    /// </summary>
    private static bool NamesThisChassis(string origin)
    {
        if (origin.Length == 0) return false;

        string model = AppConfig.GetModelShort();
        if (model.Length == 0) return false;

        // A render the user supplied themselves carries no chassis folder to check, and
        // was a deliberate choice in the first place.
        if (string.Equals(Path.GetFileName(origin), "device.png", StringComparison.OrdinalIgnoreCase)) return true;

        string family = Path.GetFileName(Path.GetDirectoryName(origin) ?? string.Empty);
        return family.Length >= MinimumFamilyLength
            && model.StartsWith(family, StringComparison.OrdinalIgnoreCase);
    }

    private static ImageSource LoadCacheFile()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(CacheFile);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = DecodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static ImageSource? ReadCache(string stamp)
    {
        try
        {
            if (!File.Exists(CacheFile) || !File.Exists(CacheStampFile)) return null;
            if (!string.Equals(File.ReadAllText(CacheStampFile).Trim(), stamp, StringComparison.Ordinal)) return null;

            return LoadCacheFile();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Device render cache: " + ex.Message);
            return null;
        }
    }

    private static void WriteCache(BitmapSource render, string stamp)
    {
        try
        {
            Directory.CreateDirectory(Logger.appPath);

            string staging = CacheFile + ".tmp";
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(render));
            using (FileStream stream = File.Create(staging)) encoder.Save(stream);

            // Stamp last: a half-written pair then reads as no cache rather than as a
            // cache of the wrong picture.
            File.Move(staging, CacheFile, overwrite: true);
            File.WriteAllText(CacheStampFile, stamp);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Device render cache: " + ex.Message);
        }
    }
}
