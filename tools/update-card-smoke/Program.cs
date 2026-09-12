using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.UI;
using Arsenal.UI.ViewModels;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UpdateCardSmoke;

/// <summary>
/// Renders the application update card in each of its three states.
///
/// The card is only reachable in the running app when the signed feed actually
/// offers a newer build, and pressing its primary button replaces the executable -
/// which makes it a poor thing to exercise by hand. This harness drives the view
/// model directly instead, so the three states can be looked at without a release
/// and without installing one.
/// </summary>
internal static class Program
{
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

            var service = new StubUpdateService();

            var available = new UpdateOverlayViewModel(service);
            available.Present(SampleInfo());
            Render(available, Path.Combine(directory, "update-card-available.png"));

            var downloading = new UpdateOverlayViewModel(service);
            downloading.Present(SampleInfo());
            downloading.State = UpdateOverlayState.Downloading;
            downloading.StatusText = "Downloading and verifying the update";
            downloading.Progress = 0.42;
            downloading.PercentText = "42%";
            downloading.ProgressText = "4.9 MB of 11.8 MB";
            Render(downloading, Path.Combine(directory, "update-card-downloading.png"));

            var failed = new UpdateOverlayViewModel(service);
            failed.Present(SampleInfo());
            failed.State = UpdateOverlayState.Failed;
            failed.ErrorText = "Arsenal could not verify or start this update. Your current installation was not changed.";
            Render(failed, Path.Combine(directory, "update-card-failed.png"));

            // The progress arithmetic is the part a screenshot cannot confirm.
            var arithmetic = new UpdateOverlayViewModel(service);
            arithmetic.Present(SampleInfo());
            arithmetic.InstallCommand.Execute(null);

            // Progress<T> posts its callback to the captured context, so the report is
            // still queued at this point; let the dispatcher run it.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (arithmetic.PercentText != "50%" || arithmetic.ProgressText != "5.9 MB of 11.8 MB")
                throw new InvalidOperationException(
                    $"Progress reporting is wrong: {arithmetic.PercentText} / {arithmetic.ProgressText}");
            if (Math.Abs(arithmetic.Progress - 0.5) > 0.001)
                throw new InvalidOperationException("Progress fraction is wrong: " + arithmetic.Progress);

            Console.WriteLine("update card states rendered to " + Path.GetFullPath(directory));
            Console.WriteLine("progress arithmetic: " + arithmetic.PercentText + "  " + arithmetic.ProgressText);
            app.Shutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static UpdateInfo SampleInfo() => new()
    {
        Title = "Arsenal 1.0.1",
        CurrentVersion = "1.0.0",
        LatestVersion = "1.0.1",
        PackageBytes = 11812547,
        DownloadUrl = "https://get-arsenal.com/downloads/Arsenal-1.0.1-win-x64.exe",
        ReleaseNoteLines = new List<string>
        {
            "Replaced the generic computer symbol beside Arsenal on the About page with the official Arsenal application icon.",
            "Fixed the verified update package staying open for hashing while the installer tried to move it into place.",
            "The update card now shows the package size, the bytes received and the percentage as it downloads.",
        },
    };

    /// <summary>
    /// Renders through a real (off-screen) window rather than measuring the control on
    /// its own. The progress line animates its fill on the render loop, and a control
    /// that is never composited leaves that fill at zero - which would make every
    /// screenshot of the downloading state a lie.
    /// </summary>
    private static void Render(UpdateOverlayViewModel viewModel, string path)
    {
        var view = new Arsenal.UI.Views.Overlays.UpdateView(viewModel);
        var host = new Window
        {
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = false,
            Background = (Brush)System.Windows.Application.Current.Resources["SurfaceCard"],
            Left = -4000,
            Top = -4000,
            Content = view,
        };

        host.Show();
        host.UpdateLayout();
        PumpFrames(600);

        var source = (System.Windows.Media.Visual)host.Content;
        int width = (int)Math.Ceiling(view.ActualWidth);
        int height = (int)Math.Ceiling(view.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        var ground = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = (Brush)System.Windows.Application.Current.Resources["SurfaceCard"],
        };
        ground.Measure(new Size(width, height));
        ground.Arrange(new Rect(0, 0, width, height));
        bitmap.Render(ground);
        bitmap.Render(source);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(path)) encoder.Save(stream);

        host.Content = null;
        host.Close();
    }

    /// <summary>Lets the dispatcher and the render loop run for a while.</summary>
    private static void PumpFrames(int milliseconds)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        DateTime until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Thread.Sleep(16);
        }
    }

    /// <summary>Reports half of the sample package, then stops. Nothing is installed.</summary>
    private sealed class StubUpdateService : IUpdateService
    {
        public event Action<UpdateInfo>? UpdateStatusChanged { add { } remove { } }
        public Task<UpdateInfo> CheckForUpdatesAsync(bool force = false) => Task.FromResult(new UpdateInfo());

        public Task<bool> DownloadAndInstallUpdateAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(5906273);
            return Task.FromResult(true);
        }

        public Task<List<UpdateInfo>> CheckAsusUpdatesAsync() => Task.FromResult(new List<UpdateInfo>());

        public Task<string?> DownloadAsusPackageAsync(string downloadUrl, IProgress<int>? progress,
            CancellationToken cancellationToken, string? expectedSha256 = null) => Task.FromResult<string?>(null);

        public string DownloadFolder => AppContext.BaseDirectory;
    }
}
