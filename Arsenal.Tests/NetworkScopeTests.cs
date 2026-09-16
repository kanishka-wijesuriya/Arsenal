using System.Net;
using Arsenal.Application.Models;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// Who the companion bridge will answer at all.
/// </summary>
/// <remarks>
/// The bridge binds every interface, and the only thing deciding who reached it was a
/// Windows Firewall rule scoped to the private profile. On a network the person marked
/// private - a hotel, a conference - that is everybody on it.
///
/// The trap these tests exist to hold is the obvious fix. Refusing anything outside the
/// RFC1918 ranges also refuses a phone on an IPv6 network, where the addresses are global
/// by design and are exactly what the bridge advertises. Getting that wrong would look
/// like a security fix and arrive as "pairing stopped working at home".
/// </remarks>
public class NetworkScopeTests
{
    private static IPAddress Ip(string address) => IPAddress.Parse(address);

    private static (IPAddress, int)[] On(params (string Address, int PrefixLength)[] networks) =>
        networks.Select(network => (Ip(network.Address), network.PrefixLength)).ToArray();

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.40")]
    [InlineData("169.254.10.10")]
    [InlineData("100.96.0.1")]      // Tailscale and other overlays
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    public void NearAddressesAreAccepted(string address)
    {
        Assert.True(NetworkScope.IsPrivate(Ip(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]      // just outside 172.16/12
    [InlineData("172.15.255.255")]
    [InlineData("192.169.1.1")]
    [InlineData("100.128.0.1")]     // just outside 100.64/10
    [InlineData("2001:4860:4860::8888")]
    public void DistantAddressesAreNotPrivate(string address)
    {
        Assert.False(NetworkScope.IsPrivate(Ip(address)));
    }

    [Fact]
    public void APhoneOnTheSameGlobalIPv6NetworkIsReachable()
    {
        // The case a private-ranges-only rule gets wrong. Both addresses are global and
        // both are on the same /64, which is one room away, not the internet.
        var local = On(("2001:db8:1234:5678::1", 64));

        Assert.True(NetworkScope.IsReachablePeer(Ip("2001:db8:1234:5678::4a2b"), local));
    }

    [Fact]
    public void AGlobalIPv6AddressOnAnotherNetworkIsNot()
    {
        var local = On(("2001:db8:1234:5678::1", 64));

        Assert.False(NetworkScope.IsReachablePeer(Ip("2001:db8:1234:9999::1"), local));
    }

    [Fact]
    public void APublicIPv4PeerOnTheSameSubnetIsReachable()
    {
        // A machine with a routable address and no NAT in front of it. The phone beside it
        // is still the phone beside it.
        var local = On(("203.0.113.10", 24));

        Assert.True(NetworkScope.IsReachablePeer(Ip("203.0.113.55"), local));
    }

    [Fact]
    public void APublicIPv4PeerFromElsewhereIsRefused()
    {
        var local = On(("203.0.113.10", 24));

        Assert.False(NetworkScope.IsReachablePeer(Ip("198.51.100.7"), local));
    }

    [Fact]
    public void AnIPv4MappedPeerIsJudgedAsIPv4()
    {
        // A dual-stack socket reports IPv4 clients as ::ffff:a.b.c.d, which matches no
        // IPv6 prefix and is not in any IPv4 private range until it is unmapped.
        Assert.True(NetworkScope.IsReachablePeer(Ip("::ffff:192.168.1.40"), Array.Empty<(IPAddress, int)>()));
    }

    [Fact]
    public void NothingIsReachableWhenThereIsNothingToCompareAgainst()
    {
        Assert.False(NetworkScope.IsReachablePeer(Ip("203.0.113.55"), Array.Empty<(IPAddress, int)>()));
        Assert.False(NetworkScope.IsReachablePeer(null, Array.Empty<(IPAddress, int)>()));
    }

    [Fact]
    public void PrivateStillWinsWithoutAMatchingPrefix()
    {
        // An overlay interface is usually a /32, so another node on it shares no prefix
        // with us. The range is what recognises it.
        var local = On(("100.101.102.103", 32));

        Assert.True(NetworkScope.IsReachablePeer(Ip("100.64.9.9"), local));
    }

    [Theory]
    [InlineData("192.168.1.1", "192.168.1.250", 24, true)]
    [InlineData("192.168.1.1", "192.168.2.1", 24, false)]
    [InlineData("192.168.1.1", "192.168.2.1", 16, true)]
    [InlineData("10.0.0.1", "10.128.0.1", 9, false)]
    [InlineData("10.0.0.1", "10.128.0.1", 8, true)]
    public void PrefixComparisonHandlesPartialBytes(string left, string right, int prefix, bool expected)
    {
        Assert.Equal(expected, NetworkScope.SharesPrefix(Ip(left), Ip(right), prefix));
    }

    [Fact]
    public void AZeroLengthPrefixMatchesNothing()
    {
        // An interface that reports no prefix is saying it does not know, and "the whole
        // internet is my local network" is not the answer to read into that.
        Assert.False(NetworkScope.SharesPrefix(Ip("192.168.1.1"), Ip("8.8.8.8"), 0));
        Assert.False(NetworkScope.IsReachablePeer(Ip("8.8.8.8"), On(("192.168.1.1", 0))));
    }

    [Fact]
    public void AddressFamiliesAreNotCompared()
    {
        Assert.False(NetworkScope.SharesPrefix(Ip("192.168.1.1"), Ip("fd00::1"), 24));
    }

    [Fact]
    public void APrefixLongerThanTheAddressMatchesNothing()
    {
        Assert.False(NetworkScope.SharesPrefix(Ip("192.168.1.1"), Ip("192.168.1.1"), 33));
    }
}
