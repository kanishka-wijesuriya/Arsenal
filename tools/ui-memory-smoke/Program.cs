using Arsenal.UI;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Windows;
using Arsenal.Application.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UiMemorySmoke;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            // AppConfig prefers the file beside the executable. This keeps the smoke
            // isolated from the user's settings and prevents setup/update UI appearing.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"),
                "{\"start_minimized\":1,\"setup_version\":3,\"check_updates\":0,\"toast_enabled\":0}");

            var application = new App();
            application.InitializeComponent();
            IDeviceStateService device = NullService<IDeviceStateService>();
            IPerformanceService performance = NullService<IPerformanceService>();
            IGpuService gpu = NullService<IGpuService>();
            IBatteryService battery = NullService<IBatteryService>();
            ISettingsSearchService search = NullService<ISettingsSearchService>();
            var mainViewModel = new MainViewModel(device, performance, gpu, battery, search)
            {
                ActivePageTag = "Settings"
            };

            var services = new ServiceCollection();
            services.AddSingleton(device);
            services.AddSingleton(performance);
            services.AddSingleton(gpu);
            services.AddSingleton(battery);
            services.AddSingleton(search);
            services.AddSingleton(mainViewModel);
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<MainWindowState>();
            services.AddTransient<MainWindow>();
            ServiceProvider provider = services.BuildServiceProvider();
            typeof(App).GetProperty(nameof(App.Services), BindingFlags.Static | BindingFlags.Public)!
                .SetValue(null, provider);
            App.ApplyConfiguredTheme();
            Environment.SetEnvironmentVariable("ARSENAL_UI_LIFETIME_SMOKE_HOST", "1");

            Exception? failure = null;
            application.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await ExerciseLifetime(application);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    foreach (MainWindow window in application.Windows.OfType<MainWindow>().ToArray())
                    {
                        window.AllowClose();
                        window.Close();
                    }
                    application.Shutdown();
                }
            }));

            application.Run();
            Environment.SetEnvironmentVariable("ARSENAL_UI_LIFETIME_SMOKE_HOST", null);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task ExerciseLifetime(App application)
    {
        Assert(GetMainWindow(application) is null,
            "A minimized launch constructed the Main Window eagerly.");
        Report("cold tray");

        application.ShowMainWindow();
        MainWindow first = GetMainWindow(application)
            ?? throw new InvalidOperationException("ShowMainWindow did not retain its window.");
        await Drawn();
        Assert(ReferenceEquals(first, GetMainWindow(application)),
            "The Main Window closed before its first rendered frame.");
        Report("main shown");

        MainWindowState state = App.Services.GetRequiredService<MainWindowState>();
        Assert(state.History.Count >= 1 && state.HistoryIndex == state.History.Count - 1,
            "Navigation history was not recorded before the window went to the tray.");

        first.Hide();
        await Task.Delay(TimeSpan.FromSeconds(7));

        var pageHost = (Grid)first.FindName("PageContentHost");
        Assert(pageHost.Children.Count == 0, "The hidden page remained in the visual tree after quiet release.");
        FieldInfo cacheField = typeof(MainWindow).GetField("_pageCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Main Window page cache field was not found.");
        object cache = cacheField.GetValue(first)
            ?? throw new InvalidOperationException("Main Window page cache was unavailable.");
        int cachedPages = (int)(cache.GetType().GetProperty("Count")?.GetValue(cache) ?? -1);
        Assert(cachedPages == 0, $"The quiet release retained {cachedPages} cached page(s).");
        Report("page visuals released");

        // Reopening during the deep-idle countdown must retire the older release. This
        // is the race that would otherwise close a window underneath its return frame.
        application.ShowMainWindow();
        await Drawn();
        Assert(ReferenceEquals(first, GetMainWindow(application)),
            "A short reopen rebuilt the native Main Window instead of reusing it.");
        first.Hide();

        await Task.Delay(TimeSpan.FromSeconds(24));
        Assert(ReferenceEquals(first, GetMainWindow(application)),
            "A superseded deep-idle release closed the reopened Main Window.");

        // The replacement countdown is measured from the second Hide().
        await Task.Delay(TimeSpan.FromSeconds(8));
        Assert(GetMainWindow(application) is null,
            "The hidden native Main Window remained after the extended idle period.");
        Assert(application.MainWindow is null,
            "Application.MainWindow retained the closed window.");
        Report("native main window released");

        application.ShowMainWindow();
        await Drawn();
        MainWindow reopened = GetMainWindow(application)
            ?? throw new InvalidOperationException("Reopen did not create a Main Window.");
        Assert(!ReferenceEquals(first, reopened), "Reopen reused a closed Main Window instance.");
        Assert(App.Services.GetRequiredService<MainViewModel>().ActivePageTag == "Settings",
            "Reopen did not restore the last active page.");
        Assert(state.History.Count >= 1 && state.HistoryIndex == state.History.Count - 1,
            "Reopen lost the navigation trail.");
        Report("main reopened");

        reopened.Hide();
        ForceCollection();
        int handlesBeforeCycles = CurrentHandleCount();

        MethodInfo releaseWindow = typeof(App).GetMethod(
            "ReleaseHiddenMainWindow", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hidden Main Window release method was not found.");

        for (int cycle = 0; cycle < 8; cycle++)
        {
            Assert((bool)(releaseWindow.Invoke(application, null) ?? false),
                $"Cycle {cycle + 1} could not release the hidden Main Window.");
            application.ShowMainWindow();
            await Drawn();
            GetMainWindow(application)!.Hide();
        }

        Assert((bool)(releaseWindow.Invoke(application, null) ?? false),
            "The final repeated-cycle window could not be released.");
        ForceCollection();
        int handlesAfterCycles = CurrentHandleCount();
        Assert(handlesAfterCycles <= handlesBeforeCycles + 8,
            $"Repeated Main Window cycles retained handles: {handlesBeforeCycles} -> {handlesAfterCycles}.");

        Console.WriteLine($"UI memory smoke passed: lazy creation, quiet page release, native window release, "
            + $"page/history restore, and repeated-cycle handles {handlesBeforeCycles} -> {handlesAfterCycles}.");
    }

    private static async Task Drawn()
    {
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.Delay(350);
    }

    private static void Report(string stage)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        Console.WriteLine($"{stage}: working set {Mb(process.WorkingSet64)}, private {Mb(process.PrivateMemorySize64)}, "
            + $"managed {Mb(GC.GetTotalMemory(false))}, handles {process.HandleCount}, threads {process.Threads.Count}");
    }

    private static string Mb(long bytes) => $"{bytes / (1024 * 1024)}MB";

    private static int CurrentHandleCount()
    {
        using Process process = Process.GetCurrentProcess();
        return process.HandleCount;
    }

    private static void ForceCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static MainWindow? GetMainWindow(App application)
        => (MainWindow?)typeof(App).GetField("_mainWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(application);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T NullService<T>() where T : class
        => DispatchProxy.Create<T, NullServiceProxy>();

    public class NullServiceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Type result = targetMethod?.ReturnType ?? typeof(void);
            if (result == typeof(void)) return null;
            if (result == typeof(string)) return string.Empty;
            if (result.IsValueType) return Activator.CreateInstance(result);
            return null;
        }
    }
}
