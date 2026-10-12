using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
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

    public static byte[] CreateRequest() => [0x01, 0x00, 0x00, 0x00];

    /// <summary>Parses a reply. Returns null for anything that is not a well-formed reply with a usable MAC.</summary>
    public static UbntDiscoveryReply? TryParse(ReadOnlySpan<byte> packet, IPAddress source)
    {
        if (packet.Length < 4 || packet[0] != 0x01 || packet[1] != 0x00)
        {
            return null;
        }

        int declaredLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        var payload = packet[4..];
        if (declaredLength < payload.Length)
        {
            payload = payload[..declaredLength];
        }

        MacAddress? mac = null;
        var addresses = new List<IPAddress>();
        string? hostname = null, model = null, modelDisplay = null, firmware = null;
        bool? isDefault = null;

        var position = 0;
        while (position + 3 <= payload.Length)
        {
            var type = payload[position];
            int length = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(position + 1, 2));
            position += 3;
            if (position + length > payload.Length)
            {
                break; // truncated field: keep what was parsed so far
            }

            var value = payload.Slice(position, length);
            position += length;

            switch (type)
            {
                case FieldMac when length == 6:
                    mac ??= MacAddress.FromBytes(value);
                    break;
                case FieldMacIp when length == 10:
                    mac ??= MacAddress.FromBytes(value[..6]);
                    var address = new IPAddress(value.Slice(6, 4));
                    if (!addresses.Contains(address))
                    {
                        addresses.Add(address);
                    }

                    break;
                case FieldFirmware:
                    firmware = Text(value);
                    break;
                case FieldHostname:
                    hostname = Text(value);
                    break;
                case FieldModelShort:
                    model = Text(value);
                    break;
                case FieldModelDisplay:
                    modelDisplay = Text(value);
                    break;
                case FieldModelDisplayAlt:
                    modelDisplay ??= Text(value);
                    break;
                case FieldIsDefault when length >= 1:
                    isDefault = value[0] != 0;
                    break;
            }
        }

        if (mac is not { IsUsableUnicast: true } validMac)
        {
            return null;
        }

        return new UbntDiscoveryReply(source, validMac, addresses, hostname, model, modelDisplay, firmware, isDefault);
    }

    /// <summary>Decodes a text field defensively: printable characters only, bounded length (it ends up in logs).</summary>
    private static string? Text(ReadOnlySpan<byte> value)
    {
        var text = Encoding.UTF8.GetString(value);
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
            .GroupBy(r => (r.Mac, r.Source))
            .Select(g => g.First())
            .ToList();

        return new UbntDiscoveryResult(replies, perInterface.Any(r => r.AccessDenied));
    }

    private async Task<UbntDiscoveryResult> DiscoverOnInterfaceAsync(
        LocalInterfaceInfo nic, TimeSpan listenTime, CancellationToken cancellationToken)
    {
        var replies = new List<UbntDiscoveryReply>();
        var accessDenied = false;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.EnableBroadcast = true;
            if (OperatingSystem.IsWindows())
            {
                DisableUdpConnectionReset(socket);
            }

            socket.Bind(new IPEndPoint(nic.Address, 0));
        }
        catch (SocketException ex)
        {
            return new UbntDiscoveryResult(replies, ex.SocketErrorCode == SocketError.AccessDenied);
        }

        // Limited broadcast plus the adapter's directed broadcast address.
        var targets = new[] { IPAddress.Broadcast, nic.Subnet.BroadcastAddress }.Distinct().ToArray();
        var request = UbntDiscoveryProtocol.CreateRequest();

        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listenCts.CancelAfter(listenTime);
        var receiveTask = ReceiveLoopAsync(socket, replies, listenCts.Token);

        // Send twice, a moment apart, in case a packet is lost on a busy or wireless link.
        for (var attempt = 0; attempt < 2 && !listenCts.IsCancellationRequested; attempt++)
        {
            foreach (var target in targets)
            {
                try
                {
                    await socket.SendToAsync(request, SocketFlags.None, new IPEndPoint(target, _port), listenCts.Token)
                        .ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    accessDenied |= ex.SocketErrorCode == SocketError.AccessDenied;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), listenCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await receiveTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        lock (replies)
        {
            return new UbntDiscoveryResult(replies.ToList(), accessDenied);
        }
    }

    private static async Task ReceiveLoopAsync(Socket socket, List<UbntDiscoveryReply> replies, CancellationToken token)
    {
        var buffer = new byte[2048];
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var source = ((IPEndPoint)received.RemoteEndPoint).Address;
            var reply = UbntDiscoveryProtocol.TryParse(buffer.AsSpan(0, received.ReceivedBytes), source);
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
    [SupportedOSPlatform("windows")]
    private static void DisableUdpConnectionReset(Socket socket)
    {
        const int SioUdpConnReset = unchecked((int)0x9800000C);
        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
            // Not fatal; the receive loop also tolerates ConnectionReset.
        }
    }
}
