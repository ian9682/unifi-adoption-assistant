using System.Net;
using System.Net.Sockets;
using UniFiAdoption.Core.Device;

namespace UniFiAdoption.Core.Workflow;

public sealed record ControllerReachability(bool NameResolved, IReadOnlyList<IPAddress> Addresses, bool PortReachable, string? Problem)
{
    public override string ToString() =>
        $"name resolved={NameResolved} [{string.Join(", ", Addresses)}], TCP port reachable={PortReachable}" +
        (Problem is null ? string.Empty : $", problem: {Problem}");
}

public interface IControllerProbe
{
    Task<ControllerReachability> ProbeAsync(ControllerTarget controller, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Checks from this computer whether the controller name resolves and its inform port accepts TCP
/// connections. This is only a hint: it does not prove the access point can reach the controller.
/// </summary>
public sealed class NetworkControllerProbe : IControllerProbe
{
    public async Task<ControllerReachability> ProbeAsync(ControllerTarget controller, TimeSpan timeout, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        using (var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            dnsCts.CancelAfter(timeout);
            try
            {
                addresses = controller.HostIsIpAddress
                    ? [IPAddress.Parse(controller.Host)]
                    : await Dns.GetHostAddressesAsync(controller.Host, AddressFamily.InterNetwork, dnsCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ControllerReachability(false, [], false, "Looking up the controller name timed out.");
            }
            catch (SocketException ex)
            {
                return new ControllerReachability(false, [], false, $"The controller name could not be resolved ({ex.SocketErrorCode}).");
            }
        }

        if (addresses.Length == 0)
        {
            return new ControllerReachability(false, [], false, "The controller name resolved to no IPv4 address.");
        }

        string? lastProblem = null;
        foreach (var address in addresses)
        {
            using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tcpCts.CancelAfter(timeout);
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await tcp.ConnectAsync(address, controller.Port, tcpCts.Token).ConfigureAwait(false);
                return new ControllerReachability(true, addresses, true, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastProblem = $"No answer from {address}:{controller.Port}.";
            }
            catch (SocketException ex)
            {
                lastProblem = $"{address}:{controller.Port} {ex.SocketErrorCode}.";
            }
        }

        return new ControllerReachability(true, addresses, false, lastProblem);
    }
}
