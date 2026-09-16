using System.IO;
using Arsenal.Helpers;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// Resolving a Windows tool by name instead of letting the loader search for it.
/// </summary>
/// <remarks>
/// Starting a process by bare name searches the calling executable's own folder before
/// System32. Arsenal is portable, so that folder is usually Downloads - and some of these
/// calls are made by the elevated helper, where "netsh" meaning a stranger's binary next
/// to Arsenal.exe is an administrator running it.
///
/// <para>The other half matters as much: callers pass full paths to ASUS executables
/// through the same helper, and those must come back untouched.</para>
/// </remarks>
public class SystemPathTests
{
    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public void PowerShellResolvesToTheWindowsCopy()
    {
        string resolved = ProcessHelper.SystemPath("powershell");

        Assert.Equal(Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe"), resolved);
        Assert.True(File.Exists(resolved), "The resolved PowerShell should be a real file.");
    }

    [Theory]
    [InlineData("netsh", "netsh.exe")]
    [InlineData("shutdown", "shutdown.exe")]
    [InlineData("powercfg", "powercfg.exe")]
    [InlineData("cmd", "cmd.exe")]
    [InlineData("control", "control.exe")]
    [InlineData("msiexec", "msiexec.exe")]
    public void SystemToolsResolveIntoSystem32(string name, string expected)
    {
        Assert.Equal(Path.Combine(System32, expected), ProcessHelper.SystemPath(name));
    }

    [Fact]
    public void ExplorerResolvesIntoTheWindowsFolder()
    {
        // Not System32, unlike every other tool here.
        Assert.Equal(Path.Combine(Windows, "explorer.exe"), ProcessHelper.SystemPath("explorer"));
    }

    [Fact]
    public void AnExtensionOnTheNameMakesNoDifference()
    {
        Assert.Equal(ProcessHelper.SystemPath("netsh"), ProcessHelper.SystemPath("netsh.exe"));
        Assert.Equal(ProcessHelper.SystemPath("explorer"), ProcessHelper.SystemPath("explorer.exe"));
    }

    [Fact]
    public void NameResolutionIsNotCaseSensitive()
    {
        Assert.Equal(ProcessHelper.SystemPath("netsh"), ProcessHelper.SystemPath("NETSH"));
    }

    [Fact]
    public void AFullPathIsReturnedUnchanged()
    {
        // VisualControl and InputDispatcher both hand full paths to ASUS executables
        // through here. Rewriting one of those would break the call.
        const string asus = @"C:\Program Files (x86)\ASUS\ARMOURY CRATE Lite Service\AsusHotkey.exe";

        Assert.Equal(asus, ProcessHelper.SystemPath(asus));
    }

    [Fact]
    public void AnUnknownNameIsReturnedUnchanged()
    {
        // Left to the loader, as before. The helper narrows the known cases; it is not a
        // gate, and it should not start failing calls it was never meant to handle.
        Assert.Equal("AsusHotkey.exe", ProcessHelper.SystemPath("AsusHotkey.exe"));
    }

    [Fact]
    public void NvidiaSmiResolvesWhenWindowsShipsItAndFallsBackWhenItDoesNot()
    {
        // Current drivers put it in System32; older ones left it under Program Files and
        // relied on PATH. Both answers are correct, and which one is right depends on the
        // machine this runs on.
        string resolved = ProcessHelper.SystemPath("nvidia-smi");
        string system32Copy = Path.Combine(System32, "nvidia-smi.exe");

        Assert.Equal(File.Exists(system32Copy) ? system32Copy : "nvidia-smi", resolved);
    }
}
