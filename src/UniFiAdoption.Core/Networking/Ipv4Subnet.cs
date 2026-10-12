using System.Net;
using System.Net.Sockets;

namespace UniFiAdoption.Core.Networking;

/// <summary>An IPv4 network such as 192.168.1.0/24.</summary>
public sealed class Ipv4Subnet
{
    public Ipv4Subnet(uint anyAddressInSubnet, int prefixLength)
    {
        if (prefixLength < 0 || prefixLength > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }

        PrefixLength = prefixLength;
        Network = anyAddressInSubnet & MaskFor(prefixLength);
    }

    public uint Network { get; }

    public int PrefixLength { get; }

    public uint Mask => MaskFor(PrefixLength);

    public uint Broadcast => Network | ~Mask;

    public IPAddress NetworkAddress => ToAddress(Network);

    public IPAddress BroadcastAddress => ToAddress(Broadcast);

    /// <summary>Number of usable host addresses.</summary>
    public long HostCount => HostCountFor(PrefixLength);

    public static uint MaskFor(int prefixLength) => prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);

    public static long HostCountFor(int prefixLength) => prefixLength switch
    {
        32 => 1,
        31 => 2,
        _ => (1L << (32 - prefixLength)) - 2,
    };

    public static Ipv4Subnet FromAddressAndMask(IPAddress address, IPAddress mask)
    {
        var maskValue = ToUInt32(mask);
        var prefix = 0;
        for (var bits = maskValue; bits != 0; bits &= bits - 1)
        {
            prefix++;
        }

        if (MaskFor(prefix) != maskValue)
        {
            throw new ArgumentException("The subnet mask is not contiguous.", nameof(mask));
        }

        return new Ipv4Subnet(ToUInt32(address), prefix);
    }

    public static uint ToUInt32(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        }

        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress ToAddress(uint value)
    {
        return new IPAddress(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
    }

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && (ToUInt32(address) & Mask) == Network;

    /// <summary>All usable host addresses, excluding the network and broadcast addresses where they exist.</summary>
    public IEnumerable<IPAddress> Hosts()
    {
        if (PrefixLength >= 31)
        {
            for (long v = Network; v <= Broadcast; v++)
            {
                yield return ToAddress((uint)v);
            }

            yield break;
        }

        for (long v = (long)Network + 1; v < Broadcast; v++)
        {
            yield return ToAddress((uint)v);
        }
    }

    /// <summary>
    /// Large networks (for example a /16) would take far too long to sweep. This returns the
    /// largest subnet containing <paramref name="anchor"/> that has at most <paramref name="maxHosts"/> hosts.
    /// </summary>
    public Ipv4Subnet NarrowAround(IPAddress anchor, long maxHosts)
    {
        if (HostCount <= maxHosts)
        {
            return this;
        }

        var prefix = PrefixLength;
        while (prefix < 30 && HostCountFor(prefix) > maxHosts)
        {
            prefix++;
        }

        return new Ipv4Subnet(ToUInt32(anchor), prefix);
    }

    public override string ToString() => $"{NetworkAddress}/{PrefixLength}";
}
