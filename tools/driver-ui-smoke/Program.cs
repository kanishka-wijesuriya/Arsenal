using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.UI;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Controls;
using Arsenal.UI.Views.Pages;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DriverUiSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"),
                "{\"theme\":1,\"accent_source\":1,\"accent_color\":\"#8B5CF6\"}");

            var app = new App();
            app.InitializeComponent();

            // Several assertions show a host window and close it again. Closing the last
            // one queues an OnLastWindowClose shutdown, which only runs once something
            // pumps the dispatcher - and the ease assertion below does exactly that, so
            // the shutdown would land in the middle of the run and fail every later page
            // load. This harness ends when Main says so, not when a window closes.
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            App.ApplyConfiguredTheme();
            AssertStableOverlayScrollViewport();
            AssertEmptyTileViewportHitTesting();
            AssertSmoothScrollIsWiredUp();
            AssertStoppedEaseNeverWritesAgain();
            HomePage homePage = AssertHomeControlsHeaderAlignment();

            var viewModel = new UpdatesViewModel(new SampleUpdateService());
            viewModel.CheckUpdates().GetAwaiter().GetResult();
            if (viewModel.DriverCount != 4 || viewModel.OutdatedDriverCount != 1
                || viewModel.CurrentDriverCount != 1 || viewModel.OptionalDriverCount != 2)
                throw new InvalidOperationException("Driver summary counts do not match the rendered sample.");
            viewModel.SetDriverFilter(1);
            if (viewModel.VisibleDrivers.Count != 1 || viewModel.VisibleDrivers[0].State != UpdateState.Outdated)
                throw new InvalidOperationException("Driver filter did not isolate available updates.");
            viewModel.SetDriverFilter(0);
            var page = new UpdatesPage(viewModel) { Width = 1080, Height = 980 };
            page.Measure(new Size(1080, 980));
            page.Arrange(new Rect(0, 0, 1080, 980));
            page.UpdateLayout();

            const double dpi = 96;
            var bitmap = new RenderTargetBitmap(1080, 980, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(page);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string output = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "driver-ui-smoke.png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            using (FileStream stream = File.Create(output)) encoder.Save(stream);

            var homeBitmap = new RenderTargetBitmap(1080, 900, dpi, dpi, PixelFormats.Pbgra32);
            homeBitmap.Render(homePage);
            var homeEncoder = new PngBitmapEncoder();
            homeEncoder.Frames.Add(BitmapFrame.Create(homeBitmap));
            string homeOutput = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(output))!,
                Path.GetFileNameWithoutExtension(output) + "-home.png");
            using (FileStream stream = File.Create(homeOutput)) homeEncoder.Save(stream);

            Console.WriteLine(Path.GetFullPath(output));
            Console.WriteLine(homeOutput);
            app.Shutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private sealed class SampleUpdateService : IUpdateService
    {
        public event Action<UpdateInfo>? UpdateStatusChanged { add { } remove { } }
        public Task<UpdateInfo> CheckForUpdatesAsync(bool force = false) => Task.FromResult(new UpdateInfo());
        public Task<bool> DownloadAndInstallUpdateAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public int BacklightZoneType => (int)Arsenal.USB.AuraBacklightType.PerKey;
        public bool HasLightbar => false;
        public Task<string?> DownloadAsusPackageAsync(string downloadUrl, IProgress<int>? progress,
            CancellationToken cancellationToken, string? expectedSha256 = null) => Task.FromResult<string?>(null);
        public string DownloadFolder => AppContext.BaseDirectory;

        public Task<List<UpdateInfo>> CheckAsusUpdatesAsync() => Task.FromResult(new List<UpdateInfo>
        {
            new()
            {
                Title = "ASUS System Control Interface v3", ReleaseNotes = "Software and Utility",
                InstalledVersion = "3.1.67.0", LatestVersion = "3.1.68.0", PublishedAt = "2026/07/18",
                DownloadUrl = "https://asus.com", State = UpdateState.Outdated, IsUpdateAvailable = true
            },
            new()
            {
                Title = "NVIDIA Graphic Driver", ReleaseNotes = "VGA",
                InstalledVersion = "32.0.16.1047", LatestVersion = "32.0.15.6607", PublishedAt = "2026/06/03",
                DownloadUrl = "https://asus.com", State = UpdateState.Newer
            },
            new()
            {
                Title = "Intel HID Event Filter Driver", ReleaseNotes = "Chipset",
                LatestVersion = "2.2.2.5", PublishedAt = "2025/11/14",
                DownloadUrl = "https://asus.com", State = UpdateState.NotInstalled
            },
            new()
            {
                Title = "Driver package requiring manual review", ReleaseNotes = "Other",
                LatestVersion = "1.0.0.0", PublishedAt = "2025/08/20",
                DownloadUrl = "https://asus.com", State = UpdateState.Unknown
            }
        });
    }

    private static void AssertStableOverlayScrollViewport()
    {
        double Layout(double contentHeight, out ScrollViewer viewer)
        {
            viewer = new ScrollViewer
            {
                Width = 800,
                Height = 400,
                Style = (Style)Application.Current.FindResource("PageScrollStyle"),
                Content = new Border { Height = contentHeight, HorizontalAlignment = HorizontalAlignment.Stretch }
            };
            viewer.Measure(new Size(800, 400));
            viewer.Arrange(new Rect(0, 0, 800, 400));
            viewer.UpdateLayout();
            return viewer.ViewportWidth;
        }

        double shortWidth = Layout(200, out _);
        double longWidth = Layout(1200, out ScrollViewer longViewer);
        if (Math.Abs(shortWidth - longWidth) > 0.01)
            throw new InvalidOperationException($"Overlay scrollbar changed viewport width: {shortWidth} vs {longWidth}.");

        longViewer.ScrollToVerticalOffset(120);
        longViewer.UpdateLayout();
        if (Math.Abs(longViewer.VerticalOffset - 120) > 0.01)
            throw new InvalidOperationException("Overlay scrollbar template did not preserve scrolling.");
    }

    private static HomePage AssertHomeControlsHeaderAlignment()
    {
        var page = new HomePage(null!) { Width = 1080, Height = 900 };
        page.Measure(new Size(1080, 900));
        page.Arrange(new Rect(0, 0, 1080, 900));
        page.UpdateLayout();

        var group = (Arsenal.UI.Controls.SettingsGroup)page.FindName("HomeControlsGroup");
        group.ApplyTemplate();
        var title = (FrameworkElement)group.Template.FindName("HeaderTitleHost", group);
        var actions = (FrameworkElement)group.Template.FindName("HeaderContentHost", group);
        var edit = (FrameworkElement)page.FindName("HomeControlsEditButton");

        Point titleOrigin = title.TranslatePoint(new Point(), group);
        Point actionsOrigin = actions.TranslatePoint(new Point(), group);
        double titleCenter = titleOrigin.Y + title.ActualHeight / 2;
        double actionsCenter = actionsOrigin.Y + actions.ActualHeight / 2;

        if (edit.Visibility != Visibility.Visible || Math.Abs(titleCenter - actionsCenter) > 0.5)
            throw new InvalidOperationException(
                $"Home Controls title and edit action are not centered in the same header row: " +
                $"editVisibility={edit.Visibility}, title={titleOrigin.Y:F2}+{title.ActualHeight:F2}, " +
                $"actions={actionsOrigin.Y:F2}+{actions.ActualHeight:F2}.");
        if (actionsOrigin.X <= titleOrigin.X + title.ActualWidth)
            throw new InvalidOperationException("Home Controls edit action overlaps the section title.");

        return page;
    }

    private static void AssertEmptyTileViewportHitTesting()
    {
        // Mirrors the Quick Panel's Border -> Canvas tile viewport. The test point is
        // deliberately in empty space with no tile beneath it.
        var viewport = new Border
        {
            Width = 420,
            Height = 280,
            Background = Brushes.Transparent,
            Child = new Canvas()
        };
        var host = new Window
        {
            Width = 420,
            Height = 280,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Content = viewport
        };
        try
        {
            // InputHitTest requires a presentation source; a detached visual tree
            // intentionally returns null even when its Background is painted.
            host.Show();
            host.UpdateLayout();
            if (viewport.InputHitTest(new Point(viewport.ActualWidth - 10, viewport.ActualHeight - 10)) is null)
                throw new InvalidOperationException("The empty tile viewport is not hit-testable for wheel input.");
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>
    /// A composed transition finishes when its longest ease completes, and that completion
    /// handler stops the siblings. Those siblings are already scheduled for the same frame,
    /// so a stopped ease must write nothing - it used to write its start value back and
    /// undo everything the completion handler had just set.
    /// </summary>
    private static void AssertStoppedEaseNeverWritesAgain()
    {
        var follower = new Arsenal.UI.Controls.FrameEase();
        double followerValue = 0;

        follower.Start(0, 100, 1000, Arsenal.UI.Controls.FrameEase.Linear, v => followerValue = v);

        // What a sibling's completion handler does: stop this ease, then write the value
        // it decided on.
        follower.Stop();
        followerValue = 42;

        // WPF invokes a snapshot of CompositionTarget.Rendering, so an ease stopped from
        // inside one handler still receives this frame's invocation. Delivering it by
        // hand is the same call WPF would make, without needing a live render loop -
        // pumping the shared application dispatcher here tears the harness down.
        MethodInfo onRendering = typeof(Arsenal.UI.Controls.FrameEase)
            .GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FrameEase.OnRendering was not found.");
        onRendering.Invoke(follower, new object?[] { null, EventArgs.Empty });

        if (Math.Abs(followerValue - 42) > 0.0001)
            throw new InvalidOperationException(
                $"A stopped FrameEase wrote again: value is {followerValue}, expected 42.");
    }

    private static void AssertSmoothScrollIsWiredUp()
    {
        var viewer = new ScrollViewer
        {
            Width = 800,
            Height = 400,
            Style = (Style)Application.Current.FindResource("PageScrollStyle"),
            Content = new Border { Height = 2400, HorizontalAlignment = HorizontalAlignment.Stretch }
        };
        var host = new Window
        {
            Width = 800,
            Height = 400,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = viewer
        };

        try
        {
            host.Show();
            host.UpdateLayout();

            if (!SmoothScroll.GetIsEnabled(viewer))
                throw new InvalidOperationException(
                    "Page scroll style no longer enables SmoothScroll.");

            AssertTouchpadScrollTracksTheDisplay(viewer);
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>
    /// A precision touchpad reports about 125 times a second, which is slower than a
    /// gaming panel refreshes. Writing each packet straight to the offset would therefore
    /// leave most presented frames showing the page exactly where the one before did -
    /// indistinguishable from a low frame rate - so a packet has to move a target that
    /// the render loop then draws out over the frames the display actually presents.
    /// </summary>
    private static void AssertTouchpadScrollTracksTheDisplay(ScrollViewer viewer)
    {
        if (viewer.Content is not UIElement content)
            throw new InvalidOperationException("Scroll content is not a visual element.");
        Matrix originalTransform = content.RenderTransform.Value;

        // 16 DIP per line is WPF's own wheel constant. Pinning it here because the value
        // that shipped before was 11, which quietly made every notch travel a third less
        // than a notch in any other app on the machine.
        double linePixels = SystemParameters.WheelScrollLines * 16d;
        const int touchpadDelta = 40;
        double expected = (touchpadDelta / 120d) * linePixels;

        viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -touchpadDelta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        });
        viewer.UpdateLayout();

        object chase = GetScrollChase(viewer);
        FieldInfo running = chase.GetType()
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ScrollChase._running was not found.");

        if (!(bool)running.GetValue(chase)!)
            throw new InvalidOperationException(
                "A touchpad packet no longer starts a per-frame chase, so scrolling is "
                + "pinned to the touchpad's report rate rather than the display's.");

        // Frames as a 240Hz panel presents them: one packet interval is about two of these.
        var positions = new List<double>();
        for (int frame = 0; frame < 200 && (bool)running.GetValue(chase)!; frame++)
        {
            AdvanceChase(chase, 1000d / 240d);
            viewer.UpdateLayout();
            if (positions.Count == 0 || Math.Abs(positions[^1] - viewer.VerticalOffset) > 0.01d)
                positions.Add(viewer.VerticalOffset);
        }

        if ((bool)running.GetValue(chase)!)
            throw new InvalidOperationException("The touchpad chase never settled on its target.");

        // The whole point: one packet has to produce several distinct positions. Writing
        // the packet straight to the offset produces exactly one, however fast the panel is.
        if (positions.Count < 3)
            throw new InvalidOperationException(
                $"One touchpad packet drew {positions.Count} distinct positions, expected at least 3.");

        if (positions[0] >= expected - 0.01d)
            throw new InvalidOperationException(
                $"The first frame covered the whole {expected} DIP instead of chasing it.");

        // And none of the packet may be lost on the way there.
        AssertScrollState(viewer, content, originalTransform, expected);
    }

    private static object GetScrollChase(ScrollViewer viewer)
    {
        FieldInfo field = typeof(SmoothScroll)
            .GetField("AnimatorProperty", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SmoothScroll.AnimatorProperty was not found.");

        var property = (DependencyProperty)field.GetValue(null)!;
        return viewer.GetValue(property)
            ?? throw new InvalidOperationException("The touchpad packet started no chase at all.");
    }

    /// <summary>
    /// Runs one frame of a chase without a render loop, by backdating the timestamp it
    /// measures frame time against and calling its handler directly. Pumping the real
    /// dispatcher instead would tie the assertion to this machine's refresh rate, which
    /// is the one thing the chase is not allowed to depend on.
    /// </summary>
    private static void AdvanceChase(object chase, double frameMs)
    {
        Type type = chase.GetType();
        FieldInfo timestamp = type.GetField("_lastTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ScrollChase._lastTimestamp was not found.");
        MethodInfo onRendering = type.GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ScrollChase.OnRendering was not found.");

        long elapsed = (long)(frameMs / 1000d * System.Diagnostics.Stopwatch.Frequency);
        timestamp.SetValue(chase, System.Diagnostics.Stopwatch.GetTimestamp() - elapsed);
        onRendering.Invoke(chase, new object?[] { null, EventArgs.Empty });
    }

    private static void AssertScrollState(
        ScrollViewer viewer,
        UIElement content,
        Matrix expectedTransform,
        double expectedOffset)
    {
        if (Math.Abs(viewer.VerticalOffset - expectedOffset) > 0.01d)
            throw new InvalidOperationException(
                $"Logical scroll position is {viewer.VerticalOffset}, expected {expectedOffset}.");

        if (content.RenderTransform.Value != expectedTransform)
            throw new InvalidOperationException(
                "Scrolling created an independent content transform.");

        if (viewer.Template.FindName("PART_VerticalScrollBar", viewer) is not ScrollBar scrollBar ||
            Math.Abs(scrollBar.Value - expectedOffset) > 0.01d)
            throw new InvalidOperationException(
                "Scrollbar did not track the real logical offset on the same layout frame.");
    }

}
