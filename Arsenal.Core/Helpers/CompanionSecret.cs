using System.Security.Cryptography;
using System.Text;

namespace Arsenal.Helpers;

/// <summary>
/// The wrapping around companion credentials at rest.
/// </summary>
/// <remarks>
/// The bridge's bearer tokens are what authorize every command a phone can send, and they
/// were stored as plain strings in config.json. That file is copied to
/// <c>C:\ProgramData</c> for the SYSTEM charge task to read, and ProgramData is readable
/// by every account on the machine - so one local user could lift another's tokens and
/// drive their laptop from the network.
///
/// <para>The copy no longer carries these keys (see <c>AppConfig.SyncFallbackConfig</c>),
/// and this is the second half: DPAPI at CurrentUser scope, so the value is bound to the
/// Windows account that paired the phone and is inert anywhere else - another account, a
/// backup, a roamed profile. The TLS identity has been stored this way for a while; the
/// tokens that actually authorize commands had been left behind.</para>
/// </remarks>
public static class CompanionSecret
{
    private const string Marker = "ARSNLSEC1:";

    public static bool IsProtected(string? stored) =>
        stored is not null && stored.StartsWith(Marker, StringComparison.Ordinal);

    public static string Protect(string value)
    {
        try
        {
            byte[] wrapped = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Marker + Convert.ToBase64String(wrapped);
        }
        catch (Exception ex)
        {
            // Storing it the way every previous release did is worse than this, but it is
            // not worse than losing the pairing outright, and the fallback config no
            // longer carries the key to a place other accounts can read.
            Logger.WriteLine("Companion secret could not be protected, storing unwrapped: " + ex.Message);
            return value;
        }
    }

    /// <summary>
    /// The stored value, or null when it belongs to another account and cannot be read.
    /// </summary>
    /// <remarks>
    /// A value with no marker was written by an earlier build, in the clear. It is
    /// returned as it stands so the pairing survives the upgrade; the caller rewrites it
    /// protected. <see cref="IsProtected"/> is how a caller tells the two apart.
    /// </remarks>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!IsProtected(stored)) return stored;

        try
        {
            byte[] wrapped = Convert.FromBase64String(stored[Marker.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(wrapped, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Companion secret could not be read on this account: " + ex.Message);
            return null;
        }
    }
}
