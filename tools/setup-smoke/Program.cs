using Arsenal.UI;
using Arsenal.UI.Controls;
using Arsenal.UI.Services.Remote;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Overlays;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using Border = System.Windows.Controls.Border;
using StackPanel = System.Windows.Controls.StackPanel;

namespace SetupSmoke;

/// <summary>
/// Renders the first-run wizard as it actually appears: inside the overlay host, over a
/// stand-in for the page behind it.
///
/// The wizard is only reachable on a machine that has never run Arsenal, and the panel
/// it opens over is the thing its scrim and shadow are judged against - so a control
/// measured on its own says nothing about how the modal reads. This harness drives the
/// view model directly and photographs each step through the real host, and it steps the
/// entrance by hand so the opening motion can be looked at frame by frame rather than
/// described.
/// </summary>
internal static class Program
{
    private const int WindowWidth = 1180;
    private const int WindowHeight = 780;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"), "{\"theme\":1}");

            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            App.ApplyConfiguredTheme();

            string directory = args.FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);

            RenderSteps(directory);
            RenderEntrance(directory);
            RenderExit(directory);
            TraceMotion(directory);

            // The scrim, the rim light and the badge tint are literal colours - the theme
            // brushes are frozen and a gradient stop cannot take one - so they are the
            // part of this that a dark-only render would not catch.
            // Through AppConfig, not the file: the configuration is read once at startup,
            // so rewriting config.json here changes nothing that is already loaded.
            AppConfig.Set("theme", 2);
            App.ApplyConfiguredTheme();
            RenderSteps(Path.Combine(directory, "light"));

            Console.WriteLine("setup wizard rendered to " + Path.GetFullPath(directory));
            app.Shutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    /// <summary>One frame per panel, each fully open.</summary>
    private static void RenderSteps(string directory)
    {
        string[] names = { "welcome", "startup", "experience", "asus", "phone", "finish" };

        Directory.CreateDirectory(directory);

        using var scene = new Scene();
        scene.Open();

        for (int index = 0; index < names.Length; index++)
        {
            scene.ViewModel.PreviewStep(index);
            scene.Pump(460);
            scene.Capture(Path.Combine(directory, "setup-" + index + "-" + names[index] + ".png"));
        }
    }

    /// <summary>The opening motion, sampled on real frames.</summary>
    private static void RenderEntrance(string directory)
    {
        using var scene = new Scene();
        scene.Pump(200);
        scene.Capture(Path.Combine(directory, "entrance-000ms.png"));

        Sample(scene, "entrance", directory, scene.Open, new[] { 45, 95, 150, 220, 320, 450 });
    }

    /// <summary>The closing motion, sampled the same way.</summary>
    private static void RenderExit(string directory)
    {
        using var scene = new Scene();
        scene.Open();
        scene.Pump(700);
        scene.Capture(Path.Combine(directory, "exit-000ms.png"));

        Sample(scene, "exit", directory, scene.Close, new[] { 50, 100, 160, 220, 300 });
    }

    /// <summary>
    /// Writes what the card and the scrim actually did, frame by frame.
    /// </summary>
    /// <remarks>
    /// A capture costs more than a frame, so a screenshot run can only photograph the
    /// motion about eight times a second - enough to show the endpoints and nothing about
    /// the shape between them. Reading the animated values off the template parts instead
    /// costs nothing, so this is what says whether the motion is smooth, how long it
    /// really takes, and whether any of it snaps.
    /// </remarks>
    private static void TraceMotion(string directory)
    {
        using var scene = new Scene();
        var lines = new List<string> { "phase\tms\tscrim\tcard\tscale\tliftY" };

        Trace(lines, scene, "open", scene.Open, 700);
        Trace(lines, scene, "close", scene.Close, 500);

        string path = Path.Combine(directory, "motion.tsv");
        File.WriteAllLines(path, lines);
        Console.WriteLine("motion trace: " + path);
    }

    private static void Trace(List<string> lines, Scene scene, string phase, Action start, int milliseconds)
    {
        var clock = new Stopwatch();
        var frame = new System.Windows.Threading.DispatcherFrame();

        EventHandler? onRendering = null;
        onRendering = (_, _) =>
        {
            if (clock.ElapsedMilliseconds > milliseconds)
            {
                CompositionTarget.Rendering -= onRendering;
                frame.Continue = false;
                return;
            }

            lines.Add(phase + "\t" + clock.ElapsedMilliseconds + "\t" + scene.Read());
        };

        CompositionTarget.Rendering += onRendering;
        start();
        clock.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Photographs a motion at the requested offsets.
    /// </summary>
    /// <remarks>
    /// Sampling by pumping the dispatcher between captures does not work here: the first
    /// idle pass after an overlay opens runs long enough to swallow the whole animation,
    /// so every frame it produced showed the motion already finished. Capturing from
    /// inside <see cref="CompositionTarget.Rendering"/> puts the camera on the same clock
    /// as the thing being photographed, and each file is named for the elapsed time it
    /// was actually taken at rather than the one that was asked for.
    /// </remarks>
    private static void Sample(Scene scene, string prefix, string directory, Action start, int[] offsets)
    {
        int next = 0;
        var clock = new Stopwatch();
        var frame = new System.Windows.Threading.DispatcherFrame();

        EventHandler? onRendering = null;
        onRendering = (_, _) =>
        {
            if (next >= offsets.Length)
            {
                CompositionTarget.Rendering -= onRendering;
                frame.Continue = false;
                return;
            }

            if (clock.ElapsedMilliseconds < offsets[next]) return;

            long taken = clock.ElapsedMilliseconds;
            next++;
            scene.Capture(Path.Combine(directory, prefix + "-" + taken.ToString("000") + "ms.png"));
        };

        CompositionTarget.Rendering += onRendering;
        start();
        clock.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// The wizard inside the overlay host, over a stand-in for the page behind it, in an
    /// off-screen window. A real window is required rather than a measured control: the
    /// host drives its fade and scale from CompositionTarget.Rendering, which never fires
    /// for a visual that is not composited.
    /// </summary>
    private sealed class Scene : IDisposable
    {
        private readonly Window _window;
        private readonly OverlayHost _host;

        public Scene()
        {
            var companion = new RemoteCompanionService(new EmptyServices());
            ViewModel = new SetupViewModel(companion);

            _host = new OverlayHost
            {
                PanelMaxWidth = 880,
                PanelMaxHeight = 620,
                IsLightDismissEnabled = false,
                Content = new SetupView(ViewModel),
            };

            var scene = new Grid();
            scene.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = Brush("SurfaceBase"),
            });
            scene.Children.Add(Backdrop());
            scene.Children.Add(_host);

            _window = new Window
            {
                Width = WindowWidth,
                Height = WindowHeight,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                AllowsTransparency = false,
                Background = Brush("SurfaceBase"),
                Left = -4000,
                Top = -4000,
                Content = scene,
            };

            _window.Show();
            _window.UpdateLayout();
        }

        public SetupViewModel ViewModel { get; }

        public void Open()
        {
            _host.IsOpen = true;
            _window.UpdateLayout();
        }

        public void Close() => _host.IsOpen = false;

        /// <summary>The animated values, straight off the template parts.</summary>
        public string Read()
        {
            var scrim = (FrameworkElement)_host.Template.FindName("PART_Scrim", _host);
            var card = (FrameworkElement)_host.Template.FindName("PART_Card", _host);
            var scale = (ScaleTransform)_host.Template.FindName("PART_Scale", _host);
            var lift = (TranslateTransform)_host.Template.FindName("PART_Translate", _host);

            return scrim.Opacity.ToString("0.000") + "\t"
                 + card.Opacity.ToString("0.000") + "\t"
                 + scale.ScaleX.ToString("0.0000") + "\t"
                 + lift.Y.ToString("0.00");
        }

        /// <summary>
        /// Enough of a page to judge the scrim and the shadow against - a sidebar column
        /// and a few cards, in the app's own brushes.
        /// </summary>
        private static UIElement Backdrop()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var rail = new Border { Background = Brush("SurfaceSunken") };
            Grid.SetColumn(rail, 0);
            grid.Children.Add(rail);

            var cards = new StackPanel { Margin = new Thickness(32, 72, 32, 32) };
            for (int i = 0; i < 4; i++)
            {
                cards.Children.Add(new Border
                {
                    Height = 92,
                    Margin = new Thickness(0, 0, 0, 16),
                    CornerRadius = (CornerRadius)System.Windows.Application.Current.Resources["RadiusCard"],
                    Background = Brush("SurfaceCard"),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brush("StrokeSubtle"),
                });
            }
            Grid.SetColumn(cards, 1);
            grid.Children.Add(cards);

            return grid;
        }

        public void Capture(string path)
        {
            var bitmap = new RenderTargetBitmap(WindowWidth, WindowHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render((Visual)_window.Content);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }

        /// <summary>Lets the dispatcher and the render loop run for a while.</summary>
        public void Pump(int milliseconds)
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            DateTime until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until)
            {
                dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Thread.Sleep(8);
            }
        }

        public void Dispose()
        {
            _window.Content = null;
            _window.Close();
        }

        private static Brush Brush(string key)
            => (Brush)System.Windows.Application.Current.Resources[key];
    }

    /// <summary>The companion service only stores its provider; nothing here is resolved.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
