using Arsenal.Application.Services.Contracts;
using Arsenal.Application.Services.Implementations;
using Microsoft.Extensions.DependencyInjection;

namespace Arsenal.Application.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddArsenalApplicationServices(this IServiceCollection services)
        {
            services.AddSingleton<IPerformanceService, PerformanceService>();
            services.AddSingleton<IGpuService, GpuService>();
            services.AddSingleton<IDisplayService, DisplayService>();
            services.AddSingleton<IBatteryService, BatteryService>();
            services.AddSingleton<ICoolingService, CoolingService>();
            services.AddSingleton<ILightingService, LightingService>();
            services.AddSingleton<IPeripheralService, PeripheralService>();
            services.AddSingleton<IInputDeviceService, InputDeviceService>();
            services.AddSingleton<IUpdateService, UpdateService>();
            services.AddSingleton<IProfileService, ProfileService>();
            services.AddSingleton<IDeviceStateService, DeviceStateService>();
            services.AddSingleton<ISettingsSearchService, SettingsSearchService>();

            return services;
        }
    }
}
