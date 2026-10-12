using System.Net;
using System.Net.Sockets;
using System.Text;
using UniFiAdoption.Core.Networking;

namespace UniFiAdoption.Core.Discovery;

/// <summary>
/// A reply to Ubiquiti's device discovery broadcast (UDP port 10001), the same mechanism the
/// UniFi controller and Ubiquiti's own discovery tools use. A reply proves the device runs
/// Ubiquiti firmware and tells us its MAC and IP address before any credentials are sent.
/// </summary>
public sealed record UbntDiscoveryReply(
    IPAddress Source,
    MacAddress Mac,
    IReadOnlyList<IPAddress> ReportedAddresses,
    string? Hostname,
    string? Model,
    string? ModelDisplay,
    string? Firmware,
    bool? IsFactoryDefault)
{
    public override string ToString() =>
        $"{Mac} from {Source} (reported {string.Join(", ", ReportedAddresses)}) model={Model ?? "?"}/{ModelDisplay ?? "?"} " +
        $"firmware={Firmware ?? "?"} hostname={Hostname ?? "?"} default={IsFactoryDefault?.ToString() ?? "?"}";
}

public sealed record UbntDiscoveryResult(IReadOnlyList<UbntDiscoveryReply> Replies, bool AccessDenied);

public interface IUbntDiscovery
{
    Task<UbntDiscoveryResult> DiscoverAsync(IReadOnlyList<LocalInterfaceInfo> interfaces, TimeSpan listenTime, CancellationToken cancellationToken);
}

/// <summary>
/// Version 1 of the discovery protocol: request is 01 00 00 00; the reply is a 4-byte header
/// (version, command, big-endian payload length) followed by type/length/value fields.
/// Field meanings below come from public descriptions of the protocol; only the MAC and
/// address fields are used for decisions, the rest is logged for IT.
/// </summary>
public static class UbntDiscoveryProtocol
{
    public const int DefaultPort = 10001;

    private const byte FieldMac = 0x01;
    private const byte FieldMacIp = 0x02;
    private const byte FieldFirmware = 0x03;
    private const byte FieldHostname = 0x0B;
    private const byte FieldModelShort = 0x0C;
    private const byte FieldModelDisplay = 0x14;
    private const byte FieldModelDisplayAlt = 0x15;
    private const byte FieldIsDefault = 0x17;

    public static byte[] CreateRequest() => new byte[] { 0x01, 0x00, 0x00, 0x00 };

    /// <summary>Parses a reply. Returns null for anything that is not a well-formed reply with a usable MAC.</summary>
    public static UbntDiscoveryReply? TryParse(byte[] packet, int length, IPAddress source)
    {
        if (packet is null || length < 4 || length > packet.Length || packet[0] != 0x01 || packet[1] != 0x00)
        {
            return null;
        }

        var declared = (packet[2] << 8) | packet[3];
        var end = Math.Min(length, 4 + declared);

        MacAddress? mac = null;
        var addresses = new List<IPAddress>();
        string? hostname = null, model = null, modelDisplay = null, firmware = null;
        bool? isDefault = null;

        var position = 4;
        while (position + 3 <= end)
        {
            var type = packet[position];
            var fieldLength = (packet[position + 1] << 8) | packet[position + 2];
            position += 3;
            if (position + fieldLength > end)
            {
                break; // truncated field: keep what was parsed so far
            }

            var valueStart = position;
            position += fieldLength;

            switch (type)
            {
                case FieldMac when fieldLength == 6:
                    mac ??= MacAddress.FromBytes(packet, valueStart);
                    break;
                case FieldMacIp when fieldLength == 10:
                    mac ??= MacAddress.FromBytes(packet, valueStart);
                    var address = new IPAddress(new[] { packet[valueStart + 6], packet[valueStart + 7], packet[valueStart + 8], packet[valueStart + 9] });
                    if (!addresses.Contains(address))
                    {
                        addresses.Add(address);
                    }

                    break;
                case FieldFirmware:
                    firmware = Text(packet, valueStart, fieldLength);
                    break;
                case FieldHostname:
                    hostname = Text(packet, valueStart, fieldLength);
                    break;
                case FieldModelShort:
                    model = Text(packet, valueStart, fieldLength);
                    break;
                case FieldModelDisplay:
                    modelDisplay = Text(packet, valueStart, fieldLength);
                    break;
                case FieldModelDisplayAlt:
                    modelDisplay ??= Text(packet, valueStart, fieldLength);
                    break;
                case FieldIsDefault when fieldLength >= 1:
                    isDefault = packet[valueStart] != 0;
                    break;
            }
        }

        if (mac is null || !mac.Value.IsUsableUnicast)
        {
            return null;
        }

        return new UbntDiscoveryReply(source, mac.Value, addresses, hostname, model, modelDisplay, firmware, isDefault);
    }

    public static UbntDiscoveryReply? TryParse(byte[] packet, IPAddress source) => TryParse(packet, packet?.Length ?? 0, source);

    /// <summary>Decodes a text field defensively: printable characters only, bounded length (it ends up in logs).</summary>
    private static string? Text(byte[] packet, int start, int length)
    {
        var text = Encoding.UTF8.GetString(packet, start, length);
        var cleaned = new string(text.Where(c => !char.IsControl(c)).Take(96).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>Sends the discovery request on each adapter and collects replies for a fixed time.</summary>
public sealed class UbntDiscoveryScanner : IUbntDiscovery
{
    private readonly int _port;

    public UbntDiscoveryScanner(int port = UbntDiscoveryProtocol.DefaultPort) => _port = port;

    public async Task<UbntDiscoveryResult> DiscoverAsync(
        IReadOnlyList<LocalInterfaceInfo> interfaces, TimeSpan listenTime, CancellationToken cancellationToken)
    {
        var perInterface = await Task.WhenAll(interfaces.Select(i => DiscoverOnInterfaceAsync(i, listenTime, cancellationToken)))
            .ConfigureAwait(false);

        var replies = perInterface
            .SelectMany(r => r.Replies)
            .DistinctByKey(r => r.Mac.Hex + "|" + r.Source)
            .ToList();

        return new UbntDiscoveryResult(replies, perInterface.Any(r => r.AccessDenied));
    }

    private async Task<UbntDiscoveryResult> DiscoverOnInterfaceAsync(
        LocalInterfaceInfo nic, TimeSpan listenTime, CancellationToken cancellationToken)
    {
        var replies = new List<UbntDiscoveryReply>();
        var accessDenied = false;
        UdpClient client;
        try
        {
            client = new UdpClient(new IPEndPoint(nic.Address, 0)) { EnableBroadcast = true };
        }
        catch (SocketException ex)
        {
            return new UbntDiscoveryResult(replies, ex.SocketErrorCode == SocketError.AccessDenied);
        }

        using (client)
        {
            if (Compat.IsWindows)
            {
                DisableUdpConnectionReset(client.Client);
            }

            var receiveLoop = ReceiveLoopAsync(client, replies);
            var deadline = DateTime.UtcNow + listenTime;
            var request = UbntDiscoveryProtocol.CreateRequest();
            var targets = new[] { IPAddress.Broadcast, nic.Subnet.BroadcastAddress }.Distinct().ToArray();

            try
            {
                // Send twice, a moment apart, in case a packet is lost on a busy or wireless link.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    foreach (var target in targets)
                    {
                        try
                        {
                            await client.SendAsync(request, request.Length, new IPEndPoint(target, _port)).ConfigureAwait(false);
                        }
                        catch (SocketException ex)
                        {
                            accessDenied |= ex.SocketErrorCode == SocketError.AccessDenied;
                        }
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.WhenAny(receiveLoop, Task.Delay(remaining, cancellationToken)).ConfigureAwait(false);
                }
            }
            finally
            {
                client.Close(); // ends the pending receive
                await receiveLoop.ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (replies)
        {
            return new UbntDiscoveryResult(replies.ToList(), accessDenied);
        }
    }

    private static async Task ReceiveLoopAsync(UdpClient client, List<UbntDiscoveryReply> replies)
    {
        while (true)
        {
            UdpReceiveResult received;
            try
            {
                received = await client.ReceiveAsync().ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.IsConnectionReset() || ex.SocketErrorCode == SocketError.MessageSize)
            {
                continue;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or NullReferenceException or InvalidOperationException)
            {
                return; // socket closed
            }

            var reply = UbntDiscoveryProtocol.TryParse(received.Buffer, received.Buffer.Length, received.RemoteEndPoint.Address);
            if (reply is not null)
            {
                lock (replies)
                {
                    replies.Add(reply);
                }
            }
        }
    }

    /// <summary>
    /// Windows reports an ICMP "port unreachable" for an earlier datagram as a receive error
    /// (WSAECONNRESET). Turning that behaviour off keeps the receive loop running.
    /// </summary>
    private static void DisableUdpConnectionReset(Socket socket)
    {
        const int SioUdpConnReset = unchecked((int)0x9800000C);
        try
        {
            socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch (SocketException)
        {
            // Not fatal; the receive loop also tolerates ConnectionReset.
        }
    }
}
