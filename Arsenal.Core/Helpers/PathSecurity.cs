using System.Security.AccessControl;
using System.Security.Principal;

namespace Arsenal.Helpers;

/// <summary>
/// Whether a location on disk can be written by somebody who is not an administrator.
/// </summary>
/// <remarks>
/// Arsenal ships as a portable executable, so it usually runs from Downloads, the
/// desktop, or a folder the user made at the root of a drive - all places the user, and
/// anything running as the user, can replace the file in. That is harmless until Arsenal
/// asks Windows to run that file as SYSTEM at boot, or hands it to an elevated installer:
/// then whoever can write to the folder inherits whatever privilege the file is given.
///
/// <para>The question is answered from the ACL rather than by comparing the path against
/// a list of known-good folders, because the interesting case - a directory created under
/// <c>C:\</c>, which inherits an inherit-only Modify for Authenticated Users - looks like
/// an ordinary program folder and is not one.</para>
///
/// <para>Unreadable means unprotected. These checks exist to justify handing out
/// privilege, so an unanswerable question has to be the refusing answer.</para>
/// </remarks>
public static class PathSecurity
{
    /// <summary>
    /// Rights that let a principal replace or remove the thing at this path. Attribute
    /// writes are deliberately not here: they cannot swap a binary.
    /// </summary>
    private const FileSystemRights ReplaceRights =
        FileSystemRights.WriteData |          // also CreateFiles
        FileSystemRights.AppendData |         // also CreateDirectories
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    private static readonly WellKnownSidType[] UnprivilegedGroups =
    {
        WellKnownSidType.WorldSid,
        WellKnownSidType.BuiltinUsersSid,
        WellKnownSidType.AuthenticatedUserSid,
        WellKnownSidType.InteractiveSid,
        WellKnownSidType.BuiltinGuestsSid,
        WellKnownSidType.BuiltinPowerUsersSid,
        WellKnownSidType.AnonymousSid,
    };

    /// <summary>
    /// True when only administrators can replace this file. False when it does not
    /// exist, when its folder is writable, or when the answer cannot be read.
    /// </summary>
    public static bool IsProtectedFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            var file = new FileInfo(filePath);
            if (!file.Exists) return false;
            if (!IsProtectedDirectory(file.DirectoryName)) return false;

            return NoUnprivilegedWriter(file.GetAccessControl(AccessControlSections.Access));
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Could not read permissions for {filePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when only administrators can create or replace files in this directory.
    /// </summary>
    public static bool IsProtectedDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return false;

        try
        {
            var directory = new DirectoryInfo(directoryPath);
            if (!directory.Exists) return false;

            return NoUnprivilegedWriter(directory.GetAccessControl(AccessControlSections.Access));
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Could not read permissions for {directoryPath}: {ex.Message}");
            return false;
        }
    }

    private static bool NoUnprivilegedWriter(FileSystemSecurity security)
    {
        HashSet<string> unprivileged = UnprivilegedSids();

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if ((rule.FileSystemRights & ReplaceRights) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid) continue;
            if (!unprivileged.Contains(sid.Value)) continue;

            Logger.WriteLine($"{sid.Value} can write here, so this is not an administrator-only location");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The principals whose write access disqualifies a location: the usual unprivileged
    /// groups, plus this account itself.
    /// </summary>
    /// <remarks>
    /// The current account matters because elevation does not change the SID. A folder
    /// this user owns - their profile, or one they created under <c>C:\</c> - is writable
    /// by every unelevated process they run, including the one an attacker gets first.
    /// An administrators-only ACE is a different SID and is not collected here.
    /// </remarks>
    private static HashSet<string> UnprivilegedSids()
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (WellKnownSidType type in UnprivilegedGroups)
        {
            try { sids.Add(new SecurityIdentifier(type, null).Value); }
            catch (Exception ex) { Logger.WriteLine($"Could not resolve {type}: {ex.Message}"); }
        }

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null) sids.Add(identity.User.Value);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Could not read the current user SID: " + ex.Message);
        }

        return sids;
    }
}
