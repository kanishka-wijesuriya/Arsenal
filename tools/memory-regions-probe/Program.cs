using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

// Where a WPF application's memory actually goes, stage by stage, on this machine.
//
// The application's own report says the managed heap is fifteen megabytes and the
// loaded libraries are five hundred, and neither number is the one to cut: the heap is
// too small to matter and a mapped library is shared with every other process that
// loaded it. Everything between those two figures - which is most of what Task Manager
// shows - was unattributed until MemoryHelper.LogRegions existed.
//
// This runs that same code against a window built up in stages, so each stage's cost is
// the difference between two reports rather than a guess. The stages are chosen to
// answer one question: with a second window open over the first, which is when the
// number is worst, what is holding what.
//
// It shows real windows and has to. An off-screen window is never composited, so no
// driver work happens and no surface is ever allocated - the reading would describe a
// program that never drew anything.

var sta = new Thread(Probe);
sta.SetApartmentState(ApartmentState.STA);
sta.Start();
sta.Join();

static void Probe()
{
    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

    app.Startup += async (_, _) =>
    {
        try
        {
            Report("nothing loaded");

            // The sizes the application actually uses, because the surfaces scale with
            // them and a probe at 320x200 would answer a question nobody asked.
            var main = new Window
            {
                Width = 1180,
                Height = 780,
                Left = 40,
                Top = 40,
                Title = "regions probe: main",
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x18)),
            };

            main.Show();
            await Drawn(main);
            Report("empty main window (1180x780)");

            main.Content = BuildContent(out var animated);
            await Drawn(main);
            Report("main window with content");

            Animate(animated);
            await Drawn(main);
            Report("content animating");

            var panel = new Window
            {
                Width = 452,
                Height = 940,
                Left = 1260,
                Top = 40,
                Title = "regions probe: panel",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x18, 0x19, 0x1D)),
            };

            panel.Content = BuildContent(out _);
            panel.Show();
            await Drawn(panel);
            Report("panel open over main window");

            // The quick panel caches its tile grid as one compositor surface while it is
            // up. This is what that costs, measured rather than assumed.
            if (panel.Content is FrameworkElement panelRoot)
            {
                RenderOptions.SetCachingHint(panelRoot, CachingHint.Cache);
                panelRoot.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
            }
            await Drawn(panel);
            Report("panel with its tile grid cached");

            panel.Hide();
            await Settled();
            Report("panel hidden");

            panel.Close();
            main.Hide();
            await Settled();
            Report("both windows away");
        }
        catch (Exception ex)
        {
            Console.WriteLine("probe failed: " + ex);
        }
        finally
        {
            app.Shutdown();
        }
    };

    app.Run();
}

// Something close enough to a page of the real application to cost what one costs:
// cards with rounded borders and a gradient, a glow on the lit ones, and enough of
// them to fill the window.
static UIElement BuildContent(out UIElement animated)
{
    var grid = new WrapPanel { Margin = new Thickness(18) };

    for (int i = 0; i < 48; i++)
    {
        var card = new Border
        {
            Width = 200,
            Height = 92,
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(
                Color.FromRgb(0x23, 0x25, 0x2A), Color.FromRgb(0x1A, 0x1C, 0x20), 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "tile " + i,
                Foreground = Brushes.White,
                Margin = new Thickness(14),
            },
        };

        // A third of them lit, which is roughly what a populated quick panel looks like.
        if (i % 3 == 0)
            card.Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x3C, 0xC8, 0xD0),
                BlurRadius = 16,
                ShadowDepth = 0,
                Opacity = 0.5,
            };

        grid.Children.Add(card);
    }

    animated = grid.Children[0];

    return new ScrollViewer
    {
        Content = grid,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
}

static void Animate(UIElement element)
{
    var transform = new ScaleTransform(1, 1);
    element.RenderTransform = transform;
    element.RenderTransformOrigin = new Point(0.5, 0.5);

    var pulse = new DoubleAnimation(1.0, 1.06, TimeSpan.FromMilliseconds(700))
    {
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
    };

    transform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
    transform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
}

// Waits until the compositor has actually put frames up, so the surfaces a stage causes
// exist before they are counted.
static async Task Drawn(Window window)
{
    for (int i = 0; i < 3; i++)
    {
        var drawn = new TaskCompletionSource();
        void OnRendered(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendered;
            drawn.TrySetResult();
        }
        CompositionTarget.Rendering += OnRendered;
        window.InvalidateVisual();
        await drawn.Task;
    }

    // The driver allocates and compiles on its own threads; reading the address space
    // out from under it would describe a moment that never settles.
    await Task.Delay(1200);
}

// After something is taken away, the same release the application performs - otherwise
// the reading describes memory that was about to be handed back anyway.
static async Task Settled()
{
    await Task.Delay(600);
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    Arsenal.Helpers.MemoryHelper.TrimWorkingSet();
    await Task.Delay(600);
}

static void Report(string stage)
{
    Console.WriteLine();
    Console.WriteLine("=== " + stage + " ===");
    Arsenal.Helpers.MemoryHelper.LogReport(stage);
}

// The report writes through the application's logger, which is a file in the real
// program. Here it goes to the console, so a run says what it found without anybody
// having to go and open log.txt.
public static class Logger
{
    public static void WriteLine(string message) => Console.WriteLine(message);
}
