using Arsenal.Application.Models;
using Arsenal.Application.Services.Implementations;
using Arsenal.Helpers;

Console.WriteLine($"Model: {AppConfig.GetModel()}");
var service = new UpdateService();
List<UpdateInfo> drivers = await service.CheckAsusUpdatesAsync();

Console.WriteLine($"Drivers: {drivers.Count}");
foreach (UpdateInfo item in drivers)
    Console.WriteLine($"{item.State,-12} installed={item.InstalledVersionText,-18} available={item.LatestVersion,-24} ids={item.HardwareIds.Count,-3} {item.Title}");

if (drivers.Count == 0) return 2;
if (drivers.Any(item => item.IsHidden)) return 3;
if (drivers.Any(item => string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.LatestVersion))) return 4;
return 0;
