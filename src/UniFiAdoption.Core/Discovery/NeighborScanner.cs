using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using UniFiAdoption.Core.Networking;

namespace UniFiAdoption.Core.Discovery;

public sealed record NeighborEntry(IPAddress Address, MacAddress Mac);

public sealed record NeighborScanResult(IReadOnlyList<NeighborEntry> Entries, int AddressesProbed, bool AccessDenied);

/// <summary>Finds the MAC address behind every live IPv4 address on the local subnets.</summary>
public interface INeighborScanner
{
    Task<NeighborScanResult> ScanAsync(
        IReadOnlyList<LocalInterfaceInfo> interfaces,
        int maxHostsPerInterface,
        int parallelism,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Used on non-Windows systems (tests); finds nothing.</summary>
public sealed class NullNeighborScanner : INeighborScanner
{
    public Task<NeighborScanResult> ScanAsync(
        IReadOnlyList<LocalInterfaceInfo> interfaces, int maxHostsPerInterface, int parallelism,
        IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new NeighborScanResult(Array.Empty<NeighborEntry>(), 0, false));
}

/// <summary>
/// ARP sweep using the Windows IP Helper API <c>SendARP</c>. ARP works even when a device ignores
/// ping or has a firewall, as long as it is on the same local network segment. It needs no
/// administrator rights and no firewall changes. Calls block for up to a few seconds when nobody
/// answers, so they run on a small pool of dedicated threads.
/// </summary>
public sealed class WindowsArpScanner : INeighborScanner
{
    private const int NoError = 0;
    private const int ErrorAccessDenied = 5;

    public Task<NeighborScanResult> ScanAsync(
        IReadOnlyList<LocalInterfaceInfo> interfaces,
        int maxHostsPerInterface,
        int parallelism,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var work = new List<(IPAddress Target, IPAddress Source)>();
        foreach (var nic in interfaces)
        {
            var subnet = nic.Subnet.NarrowAround(nic.Address, maxHostsPerInterface);
            foreach (var host in subnet.Hosts())
            {
                if (!host.Equals(nic.Address))
                {
                    work.Add((host, nic.Address));
                }
            }
        }

        if (work.Count == 0)
        {
            return Task.FromResult(new NeighborScanResult(Array.Empty<NeighborEntry>(), 0, false));
        }

        var results = new ConcurrentBag<NeighborEntry>();
        var completion = new TaskCompletionSource<NeighborScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerCount = Compat.Clamp(parallelism, 1, work.Count);
        var next = -1;
        var done = 0;
        var accessDenied = 0;
        var remainingWorkers = workerCount;

        for (var w = 0; w < workerCount; w++)
        {
            var thread = new Thread(
                () =>
                {
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            var index = Interlocked.Increment(ref next);
                            if (index >= work.Count)
                            {
                                break;
                            }

                            var (target, source) = work[index];
                            var rc = TrySendArp(target, source, out var mac);
                            if (rc == NoError)
                            {
                                results.Add(new NeighborEntry(target, mac));
                            }
                            else if (rc == ErrorAccessDenied)
                            {
                                Interlocked.Increment(ref accessDenied);
                            }

                            var finished = Interlocked.Increment(ref done);
                            if (finished % 16 == 0 || finished == work.Count)
                            {
                                progress?.Report((double)finished / work.Count);
                            }
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref remainingWorkers) == 0)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                completion.TrySetCanceled(cancellationToken);
                            }
                            else
                            {
                                completion.TrySetResult(new NeighborScanResult(results.ToList(), work.Count, accessDenied > 0));
                            }
                        }
                    }
                },
                maxStackSize: 256 * 1024)
            {
                IsBackground = true,
                Name = "ARP sweep",
            };
            thread.Start();
        }

        return completion.Task;
    }

    private static int TrySendArp(IPAddress target, IPAddress source, out MacAddress mac)
    {
        mac = default;
        try
        {
            var buffer = new byte[8];
            var length = (uint)buffer.Length;

            // IPAddr is the address in network byte order as laid out in memory, which is what
            // BitConverter produces from the big-endian address bytes on little-endian Windows.
            var rc = SendARP(
                BitConverter.ToUInt32(target.GetAddressBytes(), 0),
                BitConverter.ToUInt32(source.GetAddressBytes(), 0),
                buffer,
                ref length);

            if (rc != NoError)
            {
                return rc;
            }

            if (length != 6)
            {
                return -1;
            }

            var parsed = MacAddress.FromBytes(buffer);
            if (!parsed.IsUsableUnicast)
            {
                return -1;
            }

            mac = parsed;
            return NoError;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] pMacAddr, ref uint phyAddrLen);
}
