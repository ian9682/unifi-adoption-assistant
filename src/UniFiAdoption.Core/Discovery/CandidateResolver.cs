using System.Net;
using UniFiAdoption.Core.Networking;

namespace UniFiAdoption.Core.Discovery;

/// <summary>Everything learned about one MAC address from the discovery broadcast and the ARP sweep.</summary>
public sealed record DiscoveredDevice(
    MacAddress Mac,
    IReadOnlyList<IPAddress> DiscoveryAddresses,
    IReadOnlyList<IPAddress> ArpAddresses,
    UbntDiscoveryReply? Discovery)
{
    public bool AnsweredUbiquitiDiscovery => Discovery is not null;

    public override string ToString() =>
        $"{Mac} discovery=[{string.Join(", ", DiscoveryAddresses)}] arp=[{string.Join(", ", ArpAddresses)}]";
}

public enum LocateStatus
{
    Found,
    NotFound,
    MultipleMatches,
    AmbiguousAddress,
    NotUnifiDevice,
    OnOtherNetwork,
}

public sealed record LocateResult(
    LocateStatus Status,
    DiscoveredDevice? Device,
    IPAddress? Address,
    IReadOnlyList<DiscoveredDevice> Matches,
    int DevicesSeen);

/// <summary>
/// Pure decision logic: given what answered on the network, is there exactly one device that
/// ends in the typed characters, does it look like Ubiquiti hardware, and which single
/// address should we talk to? Any doubt results in "do nothing".
/// </summary>
public static class CandidateResolver
{
    public static LocateResult Resolve(
        MacSuffix suffix,
        IReadOnlyList<UbntDiscoveryReply> discoveryReplies,
        IReadOnlyList<NeighborEntry> neighbors,
        IReadOnlyList<LocalInterfaceInfo> interfaces,
        IReadOnlyCollection<string> ubiquitiOuis,
        bool requireUbiquitiIdentity)
    {
        var devices = Merge(discoveryReplies, neighbors);
        var matches = devices.Where(d => d.Mac.IsUsableUnicast && d.Mac.EndsWith(suffix)).ToList();

        if (matches.Count == 0)
        {
            return new LocateResult(LocateStatus.NotFound, null, null, matches, devices.Count);
        }

        if (matches.Count > 1)
        {
            return new LocateResult(LocateStatus.MultipleMatches, null, null, matches, devices.Count);
        }

        var device = matches[0];
        var looksUbiquiti = device.AnsweredUbiquitiDiscovery ||
                            ubiquitiOuis.Contains(device.Mac.Oui, StringComparer.OrdinalIgnoreCase);
        if (requireUbiquitiIdentity && !looksUbiquiti)
        {
            return new LocateResult(LocateStatus.NotUnifiDevice, device, null, matches, devices.Count);
        }

        bool Reachable(IPAddress a) => interfaces.Any(i => i.Subnet.Contains(a));

        var discoveryReachable = device.DiscoveryAddresses.Where(Reachable).Distinct().ToList();
        var arpReachable = device.ArpAddresses.Where(Reachable).Distinct().ToList();

        if (discoveryReachable.Count == 1)
        {
            return new LocateResult(LocateStatus.Found, device, discoveryReachable[0], matches, devices.Count);
        }

        if (discoveryReachable.Count > 1)
        {
            // Prefer the one address that ARP independently confirms; otherwise refuse.
            var confirmed = discoveryReachable.Where(arpReachable.Contains).ToList();
            return confirmed.Count == 1
                ? new LocateResult(LocateStatus.Found, device, confirmed[0], matches, devices.Count)
                : new LocateResult(LocateStatus.AmbiguousAddress, device, null, matches, devices.Count);
        }

        if (arpReachable.Count == 1)
        {
            return new LocateResult(LocateStatus.Found, device, arpReachable[0], matches, devices.Count);
        }

        if (arpReachable.Count > 1)
        {
            return new LocateResult(LocateStatus.AmbiguousAddress, device, null, matches, devices.Count);
        }

        // Only seen through discovery, with an address outside every local subnet
        // (typically the 192.168.1.20 fallback address of an access point that got no DHCP lease).
        var foreign = device.DiscoveryAddresses.FirstOrDefault();
        return new LocateResult(LocateStatus.OnOtherNetwork, device, foreign, matches, devices.Count);
    }

    private static List<DiscoveredDevice> Merge(
        IReadOnlyList<UbntDiscoveryReply> discoveryReplies, IReadOnlyList<NeighborEntry> neighbors)
    {
        var macs = discoveryReplies.Select(r => r.Mac).Concat(neighbors.Select(n => n.Mac)).Distinct();
        var result = new List<DiscoveredDevice>();
        foreach (var mac in macs)
        {
            var replies = discoveryReplies.Where(r => r.Mac == mac).ToList();

            // The packet's source address is authoritative; the self-reported list is a fallback.
            var discoveryAddresses = replies.Select(r => r.Source)
                .Concat(replies.SelectMany(r => r.ReportedAddresses))
                .Distinct()
                .ToList();

            var arpAddresses = neighbors.Where(n => n.Mac == mac).Select(n => n.Address).Distinct().ToList();
            result.Add(new DiscoveredDevice(mac, discoveryAddresses, arpAddresses, replies.FirstOrDefault()));
        }

        return result;
    }
}
