using Arsenal.Application.Services.Contracts;
using Arsenal.UI;
using Arsenal.UI.Controls;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NavigationSmoke;

/// <summary>
/// Walks the title bar's back and forward arrows over a page and the subpages on it,
/// because the two are not symmetrical by construction and nothing else proves they
/// stay so.
///
/// The history holds page tags. A subpage is not a page and never appears in it, so
/// back carries a special case for one and forward has to carry the matching one. A
/// forward that only reads the history sails past the subpage back has just left and
/// lands on the next page, which is the fault this exists to catch.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            // Beside the executable, so nothing here touches real settings. Subpages
            // on, because a group that is not a destination has nothing to drill into.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"),
                "{\"start_minimized\":1,\"setup_version\":3,\"check_updates\":0,\"toast_enabled\":0,\"subpages\":1}");

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
            services.AddSingleton(NullService<IUpdateService>());
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<UpdatesViewModel>();
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
                    await ExerciseNavigation(application);
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

    private static async Task ExerciseNavigation(App application)
    {
        application.ShowMainWindow();
        MainWindow window = GetMainWindow(application)
            ?? throw new InvalidOperationException("ShowMainWindow did not retain its window.");
        await Drawn();

        window.NavigateToTag("Settings");
        await Drawn();
        window.NavigateToTag("Updates");
        await Drawn();
        window.NavigateToTag("Settings");
        await Drawn();

        var state = App.Services.GetRequiredService<MainWindowState>();
        Assert(state.History.Count >= 2, "Visiting several pages recorded no history.");
        Console.WriteLine($"history: {string.Join(" > ", state.History)} at {state.HistoryIndex}");

        SettingsGroup group = FirstDrillInGroup(window)
            ?? throw new InvalidOperationException("The page carries no group that can be drilled into.");
        string name = group.Header ?? "(unnamed)";

        group.Open();
        await Drawn();
        Assert(ReferenceEquals(SettingsGroup.OpenGroup, group), $"Opening {name} did not drill into it.");
        Console.WriteLine($"opened subpage: {name}");

        int indexBefore = state.HistoryIndex;

        Invoke(window, "GoBack");
        await Drawn();
        Assert(SettingsGroup.OpenGroup is null, "Back did not leave the subpage.");
        Assert(state.HistoryIndex == indexBefore, "Back out of a subpage moved the page history as well.");
        Assert(ForwardEnabled(window), "Back left the subpage with no way forward into it.");
        Console.WriteLine("back: left the subpage, page unchanged, forward offered");

        Invoke(window, "GoForward");
        await Drawn();
        Assert(ReferenceEquals(SettingsGroup.OpenGroup, group),
            "Forward did not return to the subpage back had left.");
        Assert(state.HistoryIndex == indexBefore,
            "Forward skipped the subpage and moved to another page instead.");
        Console.WriteLine($"forward: returned to {name}");

        // Leaving the page retires the offer. The group belongs to a page that is no
        // longer on screen, and forward must not drill into one nobody is looking at.
        Invoke(window, "GoBack");
        await Drawn();
        window.NavigateToTag("Updates");
        await Drawn();
        Invoke(window, "GoForward");
        await Drawn();
        Assert(SettingsGroup.OpenGroup is null,
            "Forward drilled into a subpage belonging to a page that had been left.");
        Console.WriteLine("forward after a page change: no stale subpage reopened");

        // Back still walks pages once no subpage is standing on the current one.
        int beforePageBack = state.HistoryIndex;
        Invoke(window, "GoBack");
        await Drawn();
        Assert(state.HistoryIndex == beforePageBack - 1, "Back no longer steps through pages.");
        Console.WriteLine("back: still steps through pages");

        Console.WriteLine("navigation: back and forward agree about subpages");
    }

    private static bool ForwardEnabled(MainWindow window)
        => ((System.Windows.Controls.Primitives.ButtonBase)window.FindName("NavigateForwardButton")).IsEnabled;

    private static SettingsGroup? FirstDrillInGroup(MainWindow window)
    {
        var host = (Grid)window.FindName("PageContentHost");
        return Descendants(host).OfType<SettingsGroup>().FirstOrDefault(group => group.DrillIn);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }

    private static void Invoke(MainWindow window, string method)
        => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    private static async Task Drawn()
    {
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.Delay(250);
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
