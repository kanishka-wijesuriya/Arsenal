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

    /// <summary>
    /// Replaces the running executable with a package that has already been verified.
    /// </summary>
    /// <remarks>
    /// Two things here are deliberate, and both exist because this can run elevated.
    ///
    /// <para>The script is passed as <c>-EncodedCommand</c> rather than written to a file
    /// and run with <c>-File</c>. It used to be written to the user's own profile and then
    /// handed to an elevated PowerShell, which meant anything running as the user could
    /// replace it between the write and the launch and have its own script run by the
    /// administrator prompt the person was already expecting. A command line cannot be
    /// rewritten by a medium-integrity process after the fact.</para>
    ///
    /// <para>The package is copied into the install folder and hashed <em>there</em>,
    /// rather than hashed where it was downloaded. The download folder is writable by the
    /// user; checking the hash in one folder and then installing from it left a window in
    /// which the file could be swapped between the two steps. Staged inside the folder
    /// being installed into - the one that needed elevation in the first place - the file
    /// that is verified is the file that is installed.</para>
    /// </remarks>
    private static bool LaunchInstaller(string packagePath, ReleaseUpdate release)
    {
        string executable = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
        string directory = Path.GetDirectoryName(executable) ?? throw new InvalidOperationException("The Arsenal folder is unknown.");
        string backup = executable + ".previous";
        string staged = Path.Combine(directory, "Arsenal-update.staged");
        string expectedHash = release.PackageSha256.ToUpperInvariant();

        string script = $$"""
$ErrorActionPreference = 'Stop'
$package = '{{PowerShellLiteral(packagePath)}}'
$targetExe = '{{PowerShellLiteral(executable)}}'
$backup = '{{PowerShellLiteral(backup)}}'
$staged = '{{PowerShellLiteral(staged)}}'
try {
    Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
    Copy-Item -LiteralPath $package -Destination $staged -Force
    if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne '{{expectedHash}}') { throw 'The verified update package changed before installation.' }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    Move-Item -LiteralPath $targetExe -Destination $backup -Force
    Move-Item -LiteralPath $staged -Destination $targetExe -Force
    Start-Process -FilePath $targetExe -ArgumentList '--updated'
    Remove-Item -LiteralPath $package -Force
    Remove-Item -LiteralPath $backup -Force
} catch {
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination $targetExe -Force }
    if (Test-Path -LiteralPath $targetExe) { Start-Process -FilePath $targetExe -ArgumentList '--update-failed' }
    exit 1
}
""";

        ProcessStartInfo start = new()
        {
            FileName = PowerShellPath(),
            Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + EncodeCommand(script),
            WorkingDirectory = directory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (!CanWrite(directory)) start.Verb = "runas";
        Process.Start(start);
        Environment.Exit(0);
        return true;
    }

    /// <summary>PowerShell's own encoding for -EncodedCommand: UTF-16LE, then base64.</summary>
    private static string EncodeCommand(string script) =>
        Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// The full path to Windows PowerShell, so an executable of that name sitting beside
    /// a portable Arsenal cannot be what the elevation prompt launches.
    /// </summary>
    private static string PowerShellPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

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
