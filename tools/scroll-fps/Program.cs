// Measures wheel-scroll smoothness on a real page: frame intervals and the per-frame
// pixel delta the eye actually sees. Samples are buffered - console I/O inside the loop
// alone caps observed frame rate near 30fps and would fake the very stutter we hunt.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;


namespace ScrollFps;

internal static class Program
{
    private static ScrollViewer _viewer = null!;
    private static readonly List<(double Ms, double Offset)> _samples = new(4096);
    private static readonly Stopwatch _clock = new();

    private static bool _layered;

    [STAThread]
    private static int Main(string[] args)
    {
        // --layered reproduces the Quick Panel's windowing model: AllowsTransparency puts
        // WPF on its software rasteriser for the whole window.
        _layered = args.Any(a => a.Equals("--layered", StringComparison.OrdinalIgnoreCase));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string source in new[]
        {
            "pack://application:,,,/Wpf.Ui;component/Resources/Wpf.Ui.xaml",
            "pack://application:,,,/Wpf.Ui;component/Resources/Theme/Dark.xaml",
            "pack://application:,,,/Arsenal;component/Styles/DesignTokens.xaml",
            "pack://application:,,,/Arsenal;component/Styles/Controls.xaml",
            "pack://application:,,,/Arsenal;component/Styles/Components.xaml"
        })
        {
            app.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) });
        }

        // App.xaml declares these inline rather than in a dictionary, so a plain
        // Application has to re-register them or every page fails to parse.
        app.Resources["IntEqualsConverter"] = new Arsenal.UI.Converters.IntEqualsConverter();
        app.Resources["BoolToTextConverter"] = new Arsenal.UI.Converters.BoolToTextConverter();
        app.Resources["InverseBoolConverter"] = new Arsenal.UI.Converters.InverseBoolConverter();
        app.Resources["EmptyCountToVisibleConverter"] = new Arsenal.UI.Converters.EmptyCountToVisibleConverter();
        app.Resources["InverseBoolToVisibilityConverter"] = new Arsenal.UI.Converters.InverseBoolToVisibilityConverter();
        app.Resources["NonZeroToBoolConverter"] = new Arsenal.UI.Converters.NonZeroToBoolConverter();
        app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();

        // Every real page needs a view model, so stand in a page built from the app's own
        // SettingsGroup/SettingsRow controls: same templates, same card chrome, same
        // shadows, and therefore the same per-frame layout cost.
        var stack = new StackPanel { Margin = new Thickness(24) };
        for (int g = 0; g < 24; g++)
        {
            var group = new Arsenal.UI.Controls.SettingsGroup { Header = $"Group {g + 1}" };
            for (int r = 0; r < 4; r++)
            {
                group.Items.Add(new Arsenal.UI.Controls.SettingsRow
                {
                    Header = $"Setting {r + 1}",
                    Description = "A representative description line for layout cost.",
                    Content = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = r % 2 == 0 }
                });
            }
            stack.Children.Add(group);
        }

        var page = new ScrollViewer
        {
            Style = (Style)app.Resources["PageScrollStyle"],
            Content = stack
        };

        var host = new Window
        {
            Width = 1180,
            Height = 780,
            Left = 80,
            Top = 60,
            Title = "scroll-fps",
            Content = page
        };

        if (_layered)
        {
            host.WindowStyle = WindowStyle.None;
            host.AllowsTransparency = true;
            host.Background = System.Windows.Media.Brushes.Transparent;
        }

        string[] argv = args;
        host.Loaded += (_, _) =>
        {
            host.UpdateLayout();
            _viewer = FindScrollViewer(page)
                ?? throw new InvalidOperationException("No ScrollViewer found in HomePage.");

            Console.WriteLine($"Mode              : {(_layered ? "LAYERED (AllowsTransparency=true)" : "normal HWND")}");
            Console.WriteLine($"Render tier       : {(RenderCapability.Tier >> 16)}");
            Console.WriteLine($"WheelScrollLines  : {SystemParameters.WheelScrollLines}");
            Console.WriteLine($"ScrollableHeight  : {_viewer.ScrollableHeight:F0}");
            Console.WriteLine($"Visual descendants: {CountVisuals(page)}");
            Console.WriteLine();

            CompositionTarget.Rendering += (_, _) =>
                _samples.Add((_clock.Elapsed.TotalMilliseconds, _viewer.VerticalOffset));

            string? slideMode = argv.FirstOrDefault(a => a.StartsWith("--slide="))?.Split("=")[1];
            bool isTouchpad = argv.Any(a => a.Equals("--touchpad", StringComparison.OrdinalIgnoreCase));
            if (slideMode is not null) { page.Content = null; host.Content = null; RunSlide(slideMode, stack); }
            else Run(isTouchpad);
        };

        Application.Current.MainWindow = host;
        host.Show();
        host.Activate();
        Dispatcher.Run();
        return 0;
    }

    /// <summary>
    /// Reproduces SlideToTilePage against real content: a 320ms slide of a
    /// TranslateTransform, driven either by a WPF timeline (as the app does) or by a
    /// per-presented-frame tick.
    /// </summary>
    private static void RunSlide(string mode, UIElement content)
    {
        var shift = new TranslateTransform();
        var moving = new Border
        {
            Child = content,
            RenderTransform = shift,
            CacheMode = new BitmapCache { SnapsToDevicePixels = true }
        };
        RenderOptions.SetCachingHint(moving, CachingHint.Cache);

        var clip = new Grid { ClipToBounds = true };
        clip.Children.Add(moving);
        ((Window)Application.Current.MainWindow!).Content = clip;
        clip.UpdateLayout();

        const double distance = -420d;
        const double durationMs = 320d;

        var positions = new List<(double Ms, double Y)>(4096);
        CompositionTarget.Rendering += (_, _) => positions.Add((_clock.Elapsed.TotalMilliseconds, shift.Y));

        _clock.Restart();

        if (mode == "frameease")
        {
            // The real shipped helper, not a reimplementation of it.
            var ease = new Arsenal.UI.Controls.FrameEase();
            ease.Start(0, distance, durationMs, Arsenal.UI.Controls.FrameEase.SineInOut, y => shift.Y = y);
        }
        else if (mode == "chase")
        {
            // The same mechanism the wheel scroll uses: one new value per presented
            // frame, interpolated from real elapsed time rather than a timeline clock.
            EventHandler? tick = null;
            tick = (_, _) =>
            {
                double t = Math.Min(1d, _clock.Elapsed.TotalMilliseconds / durationMs);
                // SineEase EaseInOut, matching the app's curve.
                double eased = 0.5d * (1d - Math.Cos(t * Math.PI));
                shift.Y = distance * eased;
                if (t >= 1d) CompositionTarget.Rendering -= tick;
            };
            CompositionTarget.Rendering += tick;
        }
        else
        {
            var slide = new System.Windows.Media.Animation.DoubleAnimation(
                0, distance, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new System.Windows.Media.Animation.SineEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                }
            };
            if (mode == "dfr")
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slide, 240);

            shift.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        var done = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        done.Tick += (_, _) =>
        {
            done.Stop();

            // Only frames presented while the slide was actually in flight.
            var during = positions.Where(p => p.Ms <= durationMs + 40).ToList();
            int repeats = 0;
            var steps = new List<double>();
            for (int i = 1; i < during.Count; i++)
            {
                double d = Math.Abs(during[i].Y - during[i - 1].Y);
                if (d <= 0.0001) repeats++;
                else steps.Add(d);
            }

            Console.WriteLine($"mode             : {mode}");
            Console.WriteLine($"frames presented : {during.Count} during the {durationMs:F0}ms slide");
            Console.WriteLine($"repeat frames    : {repeats}  <-- presented but value unchanged (judder)");
            if (steps.Count > 0)
            {
                var sorted = steps.OrderBy(x => x).ToList();
                double mean = steps.Average();
                double sd = Math.Sqrt(steps.Sum(s => (s - mean) * (s - mean)) / steps.Count);
                Console.WriteLine($"moving frames    : {steps.Count}");
                Console.WriteLine($"per-frame px     : min {sorted[0]:F2}  median {sorted[sorted.Count / 2]:F2}  max {sorted[^1]:F2}");
                Console.WriteLine($"step stddev      : {sd:F2}  (lower = smoother)");
            }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        };
        done.Start();
    }

    private static void Run(bool isTouchpad)
    {
        _clock.Restart();

        if (isTouchpad)
        {
            // Simulate a realistic two-finger touchpad gesture:
            // Packets every 8ms (125Hz) with varying deltas.
            int[] deltas = [ -15, -25, -45, -70, -105, -135, -160, -175, -180, -165, -145, -120, -95, -70, -50, -35, -25, -18, -12, -8, -5, -3, -1 ];
            var pad = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(8)
            };
            int index = 0;
            pad.Tick += (_, _) =>
            {
                if (index >= deltas.Length) { pad.Stop(); return; }
                int delta = deltas[index++];
                _viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent
                });
            };
            pad.Start();

            var done = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            done.Tick += (_, _) => { done.Stop(); Report(); Dispatcher.CurrentDispatcher.InvokeShutdown(); };
            done.Start();
        }
        else
        {
            // Six notches, one every 260ms: a normal continuous wheel spin.
            var wheel = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(260)
            };
            int fired = 0;
            wheel.Tick += (_, _) =>
            {
                if (++fired > 6) { wheel.Stop(); return; }
                _viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent
                });
            };
            wheel.Start();

            var done = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(2400)
            };
            done.Tick += (_, _) => { done.Stop(); Report(); Dispatcher.CurrentDispatcher.InvokeShutdown(); };
            done.Start();
        }
    }

    private static void Report()
    {
        Console.WriteLine($"frames captured: {_samples.Count}");
        if (_samples.Count < 3) return;

        var intervals = new List<double>();
        var moves = new List<double>();
        for (int i = 1; i < _samples.Count; i++)
        {
            intervals.Add(_samples[i].Ms - _samples[i - 1].Ms);
            moves.Add(Math.Abs(_samples[i].Offset - _samples[i - 1].Offset));
        }

        intervals.Sort();
        double median = intervals[intervals.Count / 2];
        double p95 = intervals[(int)(intervals.Count * 0.95)];
        double worst = intervals[^1];

        Console.WriteLine($"frame interval  median {median:F1}ms  p95 {p95:F1}ms  worst {worst:F1}ms");
        Console.WriteLine($"implied fps     median {1000 / median:F0}");
        Console.WriteLine($"long frames >20ms: {intervals.Count(i => i > 20)} of {intervals.Count}");
        Console.WriteLine($"long frames >33ms: {intervals.Count(i => i > 33)} of {intervals.Count}");

        var moving = moves.Where(m => m > 0.0001).ToList();
        Console.WriteLine();
        Console.WriteLine($"frames that moved: {moving.Count} of {moves.Count}");
        if (moving.Count > 0)
        {
            Console.WriteLine($"per-frame px    min {moving.Min():F2}  median {moving.OrderBy(x => x).ElementAt(moving.Count / 2):F2}  max {moving.Max():F2}");
            Console.WriteLine($"sub-pixel frames (<0.5px): {moving.Count(m => m < 0.5)} of {moving.Count}");
        }

        Console.WriteLine($"\ntotal travelled: {_samples[^1].Offset:F1}px");
    }

    private static int CountVisuals(DependencyObject root)
    {
        int n = 1;
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++)
            n += CountVisuals(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }
}

