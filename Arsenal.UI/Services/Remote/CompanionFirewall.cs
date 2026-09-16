using Arsenal.Helpers;
using System.Diagnostics;

namespace Arsenal.UI.Services.Remote;

/// <summary>
/// Installs narrowly scoped Windows Firewall rules for the local companion bridge.
/// Rules are private-network only, limited to the companion TCP/UDP port and local
/// subnet, and tied to this executable.
/// </summary>
public static class CompanionFirewall
{
    private const string TcpRule = "Arsenal Mobile Companion (TCP)";
    private const string UdpRule = "Arsenal Mobile Companion Discovery (UDP)";

    /// <summary>
    /// Bumped whenever the rules themselves change, so an install that was granted
    /// access under an older, narrower set replaces it rather than keeping it.
    /// </summary>
    private const int RuleVersion = 2;

    /// <summary>
    /// Where a phone is allowed to reach the bridge from, widest first.
    /// </summary>
    /// <remarks>
    /// These were scoped to <c>localsubnet</c>, which silently dropped a phone one subnet
    /// away - a second access point, a mesh node, a guest VLAN, or simply the laptop on
    /// Ethernet and the phone on Wi-Fi behind the same router - even when the two were
    /// perfectly routable. The list is now every range a phone can plausibly hold: the
    /// RFC1918 networks, the range Tailscale and other overlays hand out, link-local for
    /// a direct cable or hotspot, and the IPv6 equivalents. Anything outside these still
    /// cannot reach the bridge, and the rules stay bound to this executable and to the
    /// private firewall profile.
    /// </remarks>
    private static readonly string[] RemoteScopes =
    {
        "localsubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10,169.254.0.0/16,fc00::/7,fe80::/10",
        "localsubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10,169.254.0.0/16",
        "localsubnet"
    };

    public static bool IsAllowed => AppConfig.Is("companion_network_allowed") &&
                                    AppConfig.Get("companion_firewall_brand_version", 0) >= 1;

    /// <summary>
    /// True when access was granted under an older rule set. The bridge keeps running on
    /// the rules it already has; this only asks for them to be replaced.
    /// </summary>
    public static bool NeedsRuleUpgrade => IsAllowed && AppConfig.Get("companion_firewall_brand_version", 0) < RuleVersion;

    public static async Task<bool> RequestAccessAsync()
    {
        if (ProcessHelper.IsUserAdministrator()) return AllowPrivateNetwork();

        using Process? helper = ProcessHelper.StartElevatedAction("--allow-companion-network");
        if (helper is null) return false;
        await helper.WaitForExitAsync();
        bool allowed = helper.ExitCode == 0;
        AppConfig.Set("companion_network_allowed", allowed ? 1 : 0);
        AppConfig.Set("companion_firewall_brand_version", allowed ? RuleVersion : 0);
        if (allowed) AppConfig.Flush();
        return allowed;
    }

    /// <summary>
    /// Replaces rules left by an older release, when this process can do it without
    /// putting a prompt in front of somebody who did not ask for one. An unelevated
    /// session keeps the rules it has and the companion page offers the upgrade.
    /// </summary>
    public static void UpgradeRulesIfPossible()
    {
        if (!NeedsRuleUpgrade) return;
        if (!ProcessHelper.IsUserAdministrator())
        {
            Logger.WriteLine("Companion firewall rules predate cross-subnet support; waiting for an elevated run");
            return;
        }
        AllowPrivateNetwork();
    }

    public static bool AllowPrivateNetwork(bool persist = true)
    {
        if (!ProcessHelper.IsUserAdministrator()) return false;

        string executable = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;

        // Remove rules left by the previous executable identity. Those rules are
        // program-path-bound and cannot safely authorize the Arsenal binary.
        string legacyPrefix = string.Concat("G", "-Helper");
        RunNetsh($"advfirewall firewall delete rule name=\"{legacyPrefix} Mobile Companion (TCP)\"");
        RunNetsh($"advfirewall firewall delete rule name=\"{legacyPrefix} Mobile Companion Discovery (UDP)\"");

        bool tcp = ReplaceRule(TcpRule, "TCP", executable);
        bool udp = ReplaceRule(UdpRule, "UDP", executable);
        bool allowed = tcp && udp;
        if (persist)
        {
            AppConfig.Set("companion_network_allowed", allowed ? 1 : 0);
            AppConfig.Set("companion_firewall_brand_version", allowed ? RuleVersion : 0);
            AppConfig.Flush();
        }
        Logger.WriteLine(allowed
            ? "Companion firewall access enabled for private local networks"
            : "Companion firewall access could not be enabled");
        return allowed;
    }

    private static bool ReplaceRule(string name, string protocol, string executable)
    {
        // Delete first so moving to a new release path replaces the previous
        // program-bound rule instead of leaving duplicates behind.
        RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");

        // Older Windows builds reject address ranges they do not parse, and a rejected
        // rule would leave the companion with no access at all. Fall back through
        // narrower scopes rather than losing the rule entirely.
        foreach (string scope in RemoteScopes)
        {
            bool added = RunNetsh(
                $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow " +
                $"program=\"{executable}\" protocol={protocol} localport={RemoteCompanionService.DefaultPort} " +
                $"profile=private remoteip={scope} enable=yes");
            if (added) return true;
            Logger.WriteLine($"Companion firewall: {protocol} rule rejected for scope {scope}");
        }
        return false;
    }

    private static bool RunNetsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ProcessHelper.SystemPath("netsh.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return false;
            process.WaitForExit();
            string output = process.StandardOutput.ReadToEnd().Trim();
            string error = process.StandardError.ReadToEnd().Trim();
            if (!string.IsNullOrWhiteSpace(output)) Logger.WriteLine(output);
            if (!string.IsNullOrWhiteSpace(error)) Logger.WriteLine(error);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Companion firewall: " + ex.Message);
            return false;
        }
    }
}
