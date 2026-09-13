using Arsenal.Helpers;
using System.Diagnostics;

namespace Arsenal.AutoUpdate;

public sealed class AutoUpdateControl
{
#if !ARSENAL_STORE
    private static long _lastCheck;
#endif
    private readonly ReleaseFeedClient _feed = new();

    public event Action<ReleaseUpdate>? UpdateAvailable;
    public static event Action<string, bool>? OnVersionLabelChanged;

    public AutoUpdateControl()
    {
        string version = ReleaseVersion.CurrentDisplayString();
        string label = "Version" + $": {version}";
        OnVersionLabelChanged?.Invoke(label, false);
        Program.Bridge?.VisualiseUpdates(label);
    }

    public void CheckForUpdates()
    {
#if ARSENAL_STORE
        Logger.WriteLine("Application updates are managed by Microsoft Store");
        return;
#else
        if (AppConfig.Is("skip_updates")) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - Interlocked.Read(ref _lastCheck)) < 43200) return;
        Interlocked.Exchange(ref _lastCheck, now);
        _ = CheckAndNotifyAsync();
#endif
    }

    private async Task CheckAndNotifyAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            ReleaseUpdate release = await _feed.FetchLatestAsync();
            ReleaseVersion current = ReleaseVersion.Parse(ReleaseVersion.CurrentString());
            ReleaseVersion latest = ReleaseVersion.Parse(release.Version);
            if (latest.CompareTo(current) <= 0)
            {
                Logger.WriteLine($"Latest Arsenal version {current}");
                return;
            }

            string label = "Download" + $": {current} → {latest}";
            OnVersionLabelChanged?.Invoke(label, true);
            Program.Bridge?.VisualiseUpdates(label);
            UpdateAvailable?.Invoke(release);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Failed to check the signed Arsenal update feed: " + ex.Message);
        }
    }

    public async Task<bool> DownloadAndInstallAsync(ReleaseUpdate release, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
#if ARSENAL_STORE
        Logger.WriteLine("Application updates are managed by Microsoft Store");
        await Task.CompletedTask;
        return false;
#else
        try
        {
            string packagePath = await _feed.DownloadVerifiedPackageAsync(release, progress, cancellationToken);
            return LaunchInstaller(packagePath, release);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Arsenal update failed: " + ex.Message);
            return false;
        }
#endif
    }

    private static bool LaunchInstaller(string packagePath, ReleaseUpdate release)
    {
        string executable = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
        string directory = Path.GetDirectoryName(executable) ?? throw new InvalidOperationException("The Arsenal folder is unknown.");
        string scriptDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arsenal", "Updates", release.Version);
        Directory.CreateDirectory(scriptDirectory);
        string scriptPath = Path.Combine(scriptDirectory, "apply-update.ps1");
        string backup = executable + ".previous";
        string expectedHash = release.PackageSha256.ToUpperInvariant();

        string script = $$"""
param()
$ErrorActionPreference = 'Stop'
$package = '{{PowerShellLiteral(packagePath)}}'
$targetExe = '{{PowerShellLiteral(executable)}}'
$backup = '{{PowerShellLiteral(backup)}}'
try {
    Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne '{{expectedHash}}') { throw 'The verified update package changed before installation.' }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    Move-Item -LiteralPath $targetExe -Destination $backup -Force
    Copy-Item -LiteralPath $package -Destination $targetExe -Force
    Start-Process -FilePath $targetExe -ArgumentList '--updated'
    Remove-Item -LiteralPath $package -Force
    Remove-Item -LiteralPath $backup -Force
} catch {
    if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination $targetExe -Force }
    if (Test-Path -LiteralPath $targetExe) { Start-Process -FilePath $targetExe -ArgumentList '--update-failed' }
    exit 1
}
""";
        File.WriteAllText(scriptPath, script);

        ProcessStartInfo start = new()
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            WorkingDirectory = directory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (!CanWrite(directory)) start.Verb = "runas";
        Process.Start(start);
        Environment.Exit(0);
        return true;
    }

    private static bool CanWrite(string directory)
    {
        string test = Path.Combine(directory, ".arsenal-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (File.Create(test, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
        finally { try { File.Delete(test); } catch { } }
    }

    private static string PowerShellLiteral(string value) => value.Replace("'", "''");
}
