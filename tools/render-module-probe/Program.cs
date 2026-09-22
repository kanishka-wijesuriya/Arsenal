using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

// What a WPF window actually costs to draw, and what a DropShadowEffect adds to it.
//
// Arsenal's largest loaded library by a distance is the Intel graphics shader compiler
// at 82MB, and the application uses DropShadowEffect for the accent glow on every lit
// toggle and selected chip, plus a shadow on six windows. A WPF effect is a pixel
// shader, so the obvious theory is that the effects are what pull the compiler in - and
// the obvious theory is worth testing before anybody sacrifices how the application
// looks for it.
//
// Four stages, each rendering for real: nothing, an empty window, ordinary content, then
// the same content with a DropShadowEffect on it. Each stage prints the modules that
// arrived since the one before.
//
// It shows a small window briefly. It has to: an off-screen window is never composited,
// so nothing is drawn and no driver work happens.

var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

var sta = new Thread(Probe);
sta.SetApartmentState(ApartmentState.STA);
sta.Start();
sta.Join();

void Probe()
{
    // Optional: ask WPF to draw on the CPU. The point of the run is what that saves.
    if (Environment.GetCommandLineArgs().Contains("software"))
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Console.WriteLine("(software rendering forced)");
    }

    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

    app.Startup += async (_, _) =>
    {
        try
        {
            var content = new Border
            {
                Width = 220,
                Height = 90,
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x27, 0x28)),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = "probe",
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var window = new Window
            {
                Width = 320,
                Height = 200,
                Left = 40,
                Top = 40,
                Title = "Arsenal render probe",
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Background = Brushes.Black,
            };

            Report("before any window");

            window.Show();
            await Drawn(window);
            Report("empty window shown");

            window.Content = content;
            await Drawn(window);
            Report("ordinary content drawn");

            content.Effect = new DropShadowEffect
            {
                Color = Colors.Teal,
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.55,
            };
            await Drawn(window);
            Report("DropShadowEffect applied");

            window.Close();
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

// Waits until the compositor has actually put frames up, so the driver work a stage
// causes has happened before its modules are counted.
async Task Drawn(Window window)
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

    // The shader compiler is loaded by the driver on its own threads; give it a beat to
    // finish rather than reading the list out from under it.
    await Task.Delay(900);
}

void Report(string stage)
{
    using var me = Process.GetCurrentProcess();
    me.Refresh();

    var fresh = new List<(string Name, long Size)>();
    long total = 0;
    int count = 0;

    foreach (ProcessModule module in me.Modules)
    {
        count++;
        total += module.ModuleMemorySize;
        if (seen.Add(module.ModuleName)) fresh.Add((module.ModuleName, module.ModuleMemorySize));
    }

    Console.WriteLine();
    Console.WriteLine($"== {stage} ==");
    Console.WriteLine($"   modules {count}, mapped {total / (1024 * 1024)}MB, "
        + $"private {me.PrivateMemorySize64 / (1024 * 1024)}MB, "
        + $"workingSet {me.WorkingSet64 / (1024 * 1024)}MB");

    foreach ((string name, long size) in fresh.OrderByDescending(m => m.Size).Take(10))
        Console.WriteLine($"   + {size / (1024 * 1024),4}MB  {name}");

    if (fresh.Count > 10) Console.WriteLine($"   + {fresh.Count - 10} smaller");
}
