using System.Net;
using System.Net.Sockets;

namespace Arsenal.Application.Models
{
    /// <summary>
    /// Whether an address belongs to somebody the companion bridge should be talking to.
    /// </summary>
    /// <remarks>
    /// The bridge binds every interface and relied entirely on a Windows Firewall rule to
    /// decide who reaches it. That rule covers the private profile only, so a laptop on a
    /// network the person marked private - a hotel, a conference, a café - served pairing
    /// and commands to everyone on it.
    ///
    /// <para>"Private ranges only" is the obvious rule and it is wrong: the bridge
    /// advertises global IPv6 addresses, because on an IPv6 network that is what a phone
    /// one room away has to dial. Refusing those would break pairing on exactly the
    /// networks that are most correctly configured.</para>
    ///
    /// <para>So the question is not what an address looks like but whether it is on a
    /// network this machine is also on: the private ranges, or inside the prefix of one of
    /// this machine's own interfaces. A peer arriving from beyond the last router matches
    /// neither.</para>
    ///
    /// <para>This lives here, apart from the service, for the same reason
    /// <see cref="PairingWindow"/> does: it decides who gets to talk to the machine at all,
    /// and it should be possible to test that without a socket.</para>
    /// </remarks>
    public static class NetworkScope
    {
        /// <summary>
        /// An address in a range that only ever means "near", wherever this machine is.
        /// </summary>
        /// <remarks>
        /// Carrier-grade NAT space is in the list because that is what Tailscale and
        /// similar overlays hand out, and an overlay interface is usually a /32, so prefix
        /// matching alone would not recognise another node on it.
        /// </remarks>
        public static bool IsPrivate(IPAddress address)
        {
            if (address is null) return false;
            if (IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // Link-local and unique-local only: an address a phone can hold on the
                // same network, never one that reached us from the internet.
                return address.IsIPv6LinkLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
            }

            if (address.AddressFamily != AddressFamily.InterNetwork) return false;

            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }

        /// <summary>
        /// Whether two addresses agree for the first <paramref name="prefixLength"/> bits.
        /// </summary>
        public static bool SharesPrefix(IPAddress left, IPAddress right, int prefixLength)
        {
            if (left is null || right is null) return false;
            if (left.AddressFamily != right.AddressFamily) return false;

            byte[] a = left.GetAddressBytes();
            byte[] b = right.GetAddressBytes();
            if (a.Length != b.Length) return false;

            // A zero-length prefix matches everything, which as an answer to "are we on
            // the same network" is never what an interface meant to say.
            if (prefixLength <= 0 || prefixLength > a.Length * 8) return false;

            int wholeBytes = prefixLength / 8;
            for (int i = 0; i < wholeBytes; i++)
                if (a[i] != b[i]) return false;

            int remainingBits = prefixLength % 8;
            if (remainingBits == 0) return true;

            int mask = 0xFF << (8 - remainingBits);
            return (a[wholeBytes] & mask) == (b[wholeBytes] & mask);
        }

        /// <summary>
        /// Whether a peer is close enough to be served.
        /// </summary>
        /// <param name="remote">The address the connection arrived from.</param>
        /// <param name="localNetworks">
        /// This machine's own unicast addresses and their prefix lengths.
        /// </param>
        public static bool IsReachablePeer(IPAddress? remote, IEnumerable<(IPAddress Address, int PrefixLength)> localNetworks)
        {
            if (remote is null) return false;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            if (IsPrivate(remote)) return true;
            if (localNetworks is null) return false;

            foreach ((IPAddress address, int prefixLength) in localNetworks)
                if (SharesPrefix(address, remote, prefixLength)) return true;

            return false;
        }
    }
}
