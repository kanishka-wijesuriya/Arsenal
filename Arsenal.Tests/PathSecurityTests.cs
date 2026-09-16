using System.IO;
using Arsenal.Helpers;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The check that decides whether Arsenal will hand a location privilege.
/// </summary>
/// <remarks>
/// Arsenal ships as a portable executable, so it normally runs from Downloads or a folder
/// made at the root of a drive. Registering a boot task that runs that file as SYSTEM -
/// which is what the charge limit used to do unconditionally - hands SYSTEM to whoever can
/// replace it, and at that path the answer is everyone.
///
/// <para>A wrong answer here is expensive in both directions: too strict and the charge
/// limit silently stops being applied at boot, too lax and the escalation is back. The
/// cases below are the two ends - a folder the tests themselves can write to, and the
/// system folders that are the reason the check exists.</para>
/// </remarks>
public class PathSecurityTests
{
    [Fact]
    public void TheTempFolderIsNotProtected()
    {
        // Written to by this very test. If this says protected, the check is not reading
        // the ACL it thinks it is reading.
        string scratch = Path.Combine(Path.GetTempPath(), "arsenal-path-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            Assert.False(PathSecurity.IsProtectedDirectory(scratch));
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch { }
        }
    }

    [Fact]
    public void AFileInAWritableFolderIsNotProtected()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "arsenal-path-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string file = Path.Combine(scratch, "Arsenal.exe");
        File.WriteAllText(file, "not really an executable");

        try
        {
            Assert.False(PathSecurity.IsProtectedFile(file));
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch { }
        }
    }

    [Fact]
    public void SystemFoldersAreProtected()
    {
        // The other end of the range. These are the locations where registering a SYSTEM
        // task is reasonable, and a check that cannot recognise them refuses everything.
        Assert.True(PathSecurity.IsProtectedDirectory(Environment.GetFolderPath(Environment.SpecialFolder.System)));
        Assert.True(PathSecurity.IsProtectedDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
    }

    [Fact]
    public void AFileInASystemFolderIsProtected()
    {
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        Assert.True(File.Exists(notepad), "This test needs notepad.exe to exist.");

        Assert.True(PathSecurity.IsProtectedFile(notepad));
    }

    [Fact]
    public void TheUserProfileIsNotProtected()
    {
        // Where a portable Arsenal actually lives, most of the time.
        Assert.False(PathSecurity.IsProtectedDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsNotProtected(string? path)
    {
        Assert.False(PathSecurity.IsProtectedDirectory(path));
        Assert.False(PathSecurity.IsProtectedFile(path));
    }

    [Fact]
    public void SomethingThatDoesNotExistIsNotProtected()
    {
        // Unanswerable has to mean refuse: this check exists to justify handing out
        // privilege, so it cannot default to yes.
        string missing = Path.Combine(Path.GetTempPath(), "arsenal-missing-" + Guid.NewGuid().ToString("N"));

        Assert.False(PathSecurity.IsProtectedDirectory(missing));
        Assert.False(PathSecurity.IsProtectedFile(Path.Combine(missing, "Arsenal.exe")));
    }
}
