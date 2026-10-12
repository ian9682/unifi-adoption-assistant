using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UniFiAdoption.Core.Networking;

/// <summary>An IPv4 address assigned to one of this computer's network adapters.</summary>
public sealed record LocalInterfaceInfo(
    string Name,
    string Description,
    IPAddress Address,
    Ipv4Subnet Subnet,
    IPAddress? Gateway,
    bool IsWireless,
    bool LooksVirtual)
{
    /// <summary>169.254.x.x: Windows assigned this itself because no DHCP server answered.</summary>
    public bool IsSelfAssigned => Address.GetAddressBytes() is [169, 254, _, _];

    public override string ToString() =>
        $"{Name} ({Description}) {Address} in {Subnet}, gateway {Gateway?.ToString() ?? "none"}" +
        $"{(IsWireless ? ", Wi-Fi" : string.Empty)}{(LooksVirtual ? ", looks virtual" : string.Empty)}";
}

public interface ILocalNetworkProvider
{
    IReadOnlyList<LocalInterfaceInfo> GetIPv4Interfaces();
}

/// <summary>Reads the active adapters from the operating system. Needs no special permissions.</summary>
public sealed class SystemLocalNetworkProvider : ILocalNetworkProvider
{
    // Virtual adapters (VPNs, hypervisors, WSL) are skipped for scanning when a physical adapter exists.
    private static readonly string[] VirtualHints =
    [
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "wsl", "tap-windows",
        "wireguard", "tailscale", "zerotier", "vpn", "fortinet", "anyconnect",
    ];

    public IReadOnlyList<LocalInterfaceInfo> GetIPv4Interfaces()
    {
        var result = new List<LocalInterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var gateway = properties.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            var text = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            var looksVirtual = VirtualHints.Any(text.Contains);

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null ||
                    unicast.IPv4Mask.Equals(IPAddress.Any))
                {
                    continue;
                }

                Ipv4Subnet subnet;
                try
                {
                    subnet = Ipv4Subnet.FromAddressAndMask(unicast.Address, unicast.IPv4Mask);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                result.Add(new LocalInterfaceInfo(
                    nic.Name,
                    nic.Description,
                    unicast.Address,
                    subnet,
                    gateway,
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    looksVirtual));
            }
        }

        return result
            .OrderByDescending(i => i.Gateway is not null)
            .ThenBy(i => i.LooksVirtual)
            .ToList();
    }
}
