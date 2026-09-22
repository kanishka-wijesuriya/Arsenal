using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Arsenal.UI.Controls;

// What opening the Lighting page costs, in milliseconds, on the thread the user is
// waiting on.
//
// The complaint is that Slash settings take a long time to appear. SlashPreview bakes
// its unchanging art into a bitmap when it is arranged, and that bake is a
// RenderTargetBitmap render of a rounded lid, four hundred-odd clipped antialiased
// hatch lines and seventy slit quads. RenderTargetBitmap rasterises in software - it
// does not go near the graphics card - and it runs inside ArrangeOverride, which is the
// UI thread, which is the page appearing.
//
// Measured here at the sizes the page actually uses, and at both scales, because the
// bake is sized in device pixels and this panel is not at 100%.

var sta = new Thread(Measure);
sta.SetApartmentState(ApartmentState.STA);
sta.Start();
sta.Join();

static void Measure()
{
    // A dispatcher has to exist for a FrameworkElement to be measured at all.
    _ = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    foreach (double width in new double[] { 532, 400, 300 })
    {
        var preview = new SlashPreview { Mode = 0, Brightness = 3, Segments = 7 };

        // First arrange: the bake. This is what a page open pays.
        var first = Stopwatch.StartNew();
        preview.Measure(new Size(width, double.PositiveInfinity));
        preview.Arrange(new Rect(0, 0, width, preview.DesiredSize.Height));
        first.Stop();

        // Second arrange at the same size: the guard should make this free.
        var again = Stopwatch.StartNew();
        preview.Measure(new Size(width, double.PositiveInfinity));
        preview.Arrange(new Rect(0, 0, width, preview.DesiredSize.Height));
        again.Stop();

        // One pixel narrower, which is what a resize drag does on every frame.
        var resized = Stopwatch.StartNew();
        preview.Measure(new Size(width - 1, double.PositiveInfinity));
        preview.Arrange(new Rect(0, 0, width - 1, preview.DesiredSize.Height));
        resized.Stop();

        Console.WriteLine($"{width,4}pt wide: first arrange {first.ElapsedMilliseconds,5}ms, "
            + $"same size again {again.ElapsedMilliseconds,4}ms, "
            + $"one pixel narrower {resized.ElapsedMilliseconds,5}ms");

        preview.Dispose();
    }

    // And the frame redraw, which runs on a 90ms timer for as long as the page is open.
    var running = new SlashPreview { Mode = 2, Brightness = 3, Segments = 7 };
    running.Measure(new Size(532, double.PositiveInfinity));
    running.Arrange(new Rect(0, 0, 532, running.DesiredSize.Height));

    var ticks = Stopwatch.StartNew();
    for (int i = 0; i < 100; i++) running.Refresh();
    ticks.Stop();

    Console.WriteLine($"lit-slit redraw: {ticks.Elapsed.TotalMilliseconds / 100:F2}ms per frame");
    running.Dispose();

    // A picture of the result, so a change that makes it fast can be checked against one
    // that does not, rather than taken on trust.
    string? into = Environment.GetCommandLineArgs().SkipWhile(a => a != "--png").Skip(1).FirstOrDefault();
    if (into is null) return;

    var shot = new SlashPreview { Mode = 0, Brightness = 3, Segments = 7 };
    shot.Measure(new System.Windows.Size(532, double.PositiveInfinity));
    shot.Arrange(new Rect(0, 0, 532, shot.DesiredSize.Height));
    shot.Refresh();
    shot.UpdateLayout();

    var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
        532, (int)Math.Round(shot.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
    target.Render(shot);

    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
    using var file = System.IO.File.Create(into);
    encoder.Save(file);
    Console.WriteLine("wrote " + into);

    shot.Dispose();
}
