using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws the Arsenal mark as vector geometry in a 1024-unit square and renders it to
// every raster size the shell asks for. Keeping the source as geometry means the 16px
// tray frame is rendered from the same shape as the 256px shelf icon rather than being
// resampled down from it.
internal static class Program
{
    private const double S = 1024;

    private static readonly Color TileTop = Color.FromRgb(0x1E, 0x22, 0x26);
    private static readonly Color TileBottom = Color.FromRgb(0x0A, 0x0B, 0x0D);
    private static readonly Color AccentLight = Color.FromRgb(0x8A, 0xE2, 0xFF);
    private static readonly Color AccentDeep = Color.FromRgb(0x2E, 0x9F, 0xE8);

    [STAThread]
    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        string concept = args.Length > 1 ? args[1] : "shield";
        Directory.CreateDirectory(outDir);

        var draw = Concept(concept);

        int[] icoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = icoSizes.Select(px => (px, Render(draw, px, tile: true))).ToList();
        WriteIco(Path.Combine(outDir, "arsenal.ico"), frames);

        Save(Path.Combine(outDir, "arsenal-app-icon.png"), Render(draw, 1024, tile: true));
        Save(Path.Combine(outDir, "arsenal-mark.png"), Render(draw, 1024, tile: false));

        WriteSheet(Path.Combine(outDir, "concept-sheet.png"));
        Console.WriteLine($"wrote {concept} to {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static Action<DrawingContext> Concept(string name) => name switch
    {
        "chevron" => DrawChevronStack,
        "keystone" => DrawKeystone,
        _ => DrawShield,
    };

    // ---------------------------------------------------------------- surfaces

    private static Brush TileBrush() => Frozen(new LinearGradientBrush(TileTop, TileBottom, 90));

    private static Brush AccentBrush() => Frozen(new LinearGradientBrush(AccentLight, AccentDeep, 108));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static void DrawTile(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(TileBrush(), null, new Rect(0, 0, S, S), 228, 228);

        // A single lit edge along the top gives the tile a physical read at large sizes
        // and disappears cleanly at 16px instead of turning into a grey fringe.
        var edge = Frozen(new LinearGradientBrush(
            Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90));
        dc.DrawRoundedRectangle(null, new Pen(edge, 7), new Rect(3.5, 3.5, S - 7, S - 7), 225, 225);
    }

    // ---------------------------------------------------------------- concepts

    /// <summary>
    /// An armory shield with the boost chevron struck through it as negative space, so
    /// the mark is one solid mass at small sizes rather than a set of thin strokes.
    /// </summary>
    private static void DrawShield(DrawingContext dc)
    {
        var shield = Poly(
            (252, 196), (772, 196), (772, 566),
            (512, 880), (252, 566));

        // The arms stop well short of the shield walls: a cut that reaches them severs
        // the point into a floating diamond at every size.
        var chevron = Chevron(apexY: 306, halfWidth: 166, armDrop: 168, thickness: 108);

        var mark = new CombinedGeometry(GeometryCombineMode.Exclude, shield, chevron);
        mark.Freeze();
        dc.DrawGeometry(AccentBrush(), null, mark);
    }

    /// <summary>Two stacked chevrons - a rank insignia read, no letterform at all.</summary>
    private static void DrawChevronStack(DrawingContext dc)
    {
        const double halfWidth = 316, armDrop = 236, thickness = 148;
        dc.DrawGeometry(AccentBrush(), null, Chevron(156, halfWidth, armDrop, thickness));

        // Two solid tones rather than one tone at reduced opacity: a faded copy turns
        // grey against the tile instead of reading as a second chevron. The lower tone
        // stays bright enough to hold its own at 16px, and the two apexes are set far
        // enough apart that they do not close into a single wedge there either.
        var deep = Frozen(new LinearGradientBrush(
            Color.FromRgb(0x45, 0xB2, 0xF2), Color.FromRgb(0x21, 0x82, 0xC8), 108));
        dc.DrawGeometry(deep, null, Chevron(464, halfWidth, armDrop, thickness));
    }

    /// <summary>A solid keystone A - the monogram kept as mass, with a cut crossbar.</summary>
    private static void DrawKeystone(DrawingContext dc)
    {
        var wedge = Poly((512, 168), (846, 866), (178, 866));
        var slot = new RectangleGeometry(new Rect(330, 636, 364, 122), 34, 34);
        var mark = new CombinedGeometry(GeometryCombineMode.Exclude, wedge, slot);
        mark.Freeze();
        dc.DrawGeometry(AccentBrush(), null, mark);
    }

    // ---------------------------------------------------------------- geometry

    private static PathGeometry Chevron(double apexY, double halfWidth, double armDrop, double thickness)
    {
        double cx = S / 2;
        return Poly(
            (cx, apexY),
            (cx + halfWidth, apexY + armDrop),
            (cx + halfWidth, apexY + armDrop + thickness),
            (cx, apexY + thickness),
            (cx - halfWidth, apexY + armDrop + thickness),
            (cx - halfWidth, apexY + armDrop));
    }

    private static PathGeometry Poly(params (double x, double y)[] points)
    {
        var figure = new PathFigure { StartPoint = new Point(points[0].x, points[0].y), IsClosed = true, IsFilled = true };
        for (int i = 1; i < points.Length; i++)
            figure.Segments.Add(new LineSegment(new Point(points[i].x, points[i].y), false));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------- raster

    private static BitmapSource Render(Action<DrawingContext> draw, int px, bool tile)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(px / S, px / S));
            if (tile) DrawTile(dc);
            draw(dc);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static void Save(string path, BitmapSource bmp)
    {
        using var fs = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(fs);
    }

    /// <summary>Side-by-side concepts at the sizes that actually decide the design.</summary>
    private static void WriteSheet(string path)
    {
        string[] names = { "shield", "chevron", "keystone" };
        int[] sizes = { 256, 64, 48, 32, 16 };
        const int pad = 28, labelBand = 44;
        int rowH = 256 + pad * 2;
        int width = pad + sizes.Sum(s => Math.Max(s, 72) + pad) + 120;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1A)), null,
                new Rect(0, 0, width, rowH * names.Length + labelBand));

            var text = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC4));
            for (int r = 0; r < names.Length; r++)
            {
                var draw = Concept(names[r]);
                double y = labelBand + r * rowH + pad;
                double x = pad + 110;

                dc.DrawText(Label(names[r], 18, text), new Point(pad, y + 110));
                foreach (int s in sizes)
                {
                    var bmp = Render(draw, s, tile: true);
                    dc.DrawImage(bmp, new Rect(x, y + (256 - s) / 2.0, s, s));
                    if (r == 0) dc.DrawText(Label(s + "px", 13, text), new Point(x, 14));
                    x += Math.Max(s, 72) + pad;
                }
            }
        }

        var rtb = new RenderTargetBitmap(width, rowH * names.Length + labelBand, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        Save(path, rtb);
    }

    private static FormattedText Label(string s, double size, Brush brush) => new(
        s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), size, brush, 96);

    // ---------------------------------------------------------------- ico container

    private static void WriteIco(string path, IReadOnlyList<(int size, BitmapSource bmp)> frames)
    {
        // Sizes up to 64 go in as raw DIB: System.Drawing.Icon, which loads the tray
        // icon, is unreliable with PNG-compressed frames at those sizes. 128 and 256 go
        // in as PNG, which is what the shell expects for the large frames.
        var payloads = frames.Select(f => f.size >= 128 ? Png(f.bmp) : Dib(f.bmp, f.size)).ToList();

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)frames.Count);

        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int size = frames[i].size;
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(payloads[i].Length);
            bw.Write(offset);
            offset += payloads[i].Length;
        }

        foreach (var p in payloads) bw.Write(p);
    }

    private static byte[] Png(BitmapSource bmp)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Dib(BitmapSource bmp, int size)
    {
        // ICO stores straight (non-premultiplied) alpha; the render target is Pbgra32,
        // and this conversion is what un-premultiplies it.
        var straight = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = size * 4;
        var pixels = new byte[stride * size];
        straight.CopyPixels(pixels, stride, 0);

        int maskStride = ((size + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(40);
        bw.Write(size);
        bw.Write(size * 2);      // image plus the (unused) AND mask
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(pixels.Length);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        for (int y = size - 1; y >= 0; y--) bw.Write(pixels, y * stride, stride);
        bw.Write(new byte[maskStride * size]);

        return ms.ToArray();
    }
}
