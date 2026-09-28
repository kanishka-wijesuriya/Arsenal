using Arsenal.Application.Extensions;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Battery;
using Arsenal.Display;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Services.Remote;
using Arsenal.UI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Arsenal.UI
{
    public partial class App
    {
        private void ConfigureServices(IServiceCollection services)
        {
            // Register Application layer services
            services.AddArsenalApplicationServices();

            // Register ViewModels
            AddTrackedSingleton<MainViewModel>(services);
            AddTrackedSingleton<QuickPanelViewModel>(services);
            AddTrackedSingleton<HomeViewModel>(services);
            AddTrackedSingleton<PerformanceViewModel>(services);
            AddTrackedSingleton<DisplayViewModel>(services);
            AddTrackedSingleton<BatteryViewModel>(services);
            AddTrackedSingleton<LightingViewModel>(services);
            AddTrackedSingleton<PeripheralsViewModel>(services);
            AddTrackedSingleton<AutomationViewModel>(services);
            AddTrackedSingleton<UpdatesViewModel>(services);
            AddTrackedSingleton<AdvancedViewModel>(services);
            AddTrackedSingleton<MobileCompanionViewModel>(services);
            AddTrackedSingleton<SettingsViewModel>(services);
            AddTrackedSingleton<AboutViewModel>(services);
            services.AddSingleton<RemoteCompanionService>();

            // The companion bridge hands an upgraded connection to this; it owns no
            // socket of its own and costs nothing until a phone asks for the screen.
            services.AddSingleton<Services.Remote.Desktop.RemoteDesktopServer>();

            // Register Windows
            services.AddSingleton<MainWindowState>();
            services.AddTransient<MainWindow>();
            services.AddSingleton<QuickPanelWindow>();

            services.AddSingleton<TrayMenuWindow>();
        }

        /// <summary>
        /// Microsoft.Extensions.DependencyInjection already creates singletons on demand,
        /// but the hardware bridge used to resolve every page view model just to update a
        /// property. Keeping the Lazy wrapper registered lets bridge notifications update
        /// an existing page without constructing pages the user has never opened.
        /// </summary>
        private void AddTrackedSingleton<T>(IServiceCollection services) where T : class
        {
            services.AddSingleton(provider => new Lazy<T>(() =>
            {
                T viewModel = ActivatorUtilities.CreateInstance<T>(provider);
                ApplyPendingBridgeState(viewModel);
                return viewModel;
            }, LazyThreadSafetyMode.ExecutionAndPublication));
            services.AddSingleton(provider => provider.GetRequiredService<Lazy<T>>().Value);
        }

        private static T? GetCreatedViewModel<T>() where T : class
        {
            Lazy<T>? lazy = Services.GetService<Lazy<T>>();
            return lazy is { IsValueCreated: true } ? lazy.Value : null;
        }

        private void ApplyPendingBridgeState<T>(T viewModel) where T : class
        {
            switch (viewModel)
            {
                case MainViewModel main when _latestModeLabel is not null:
                    main.CurrentModeName = _latestModeLabel;
                    break;
                case AboutViewModel about when _latestUpdateStatus is not null:
                    about.UpdateStatus = _latestUpdateStatus;
                    break;
                case PerformanceViewModel performance when _latestCalibrationStatus is not null:
                    performance.CalibrationStatus = _latestCalibrationStatus;
                    break;
                case AdvancedViewModel advanced when _latestAsusOptimizationState.HasValue:
                    advanced.AsusOptimizationRunning = _latestAsusOptimizationState.Value;
                    break;
            }
        }
    }
}
