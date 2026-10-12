using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UniFiAdoption.Core.Ssh;

public sealed record SshBannerResult(bool Reachable, string? Banner, string? Problem);

public interface ISshBannerProbe
{
    Task<SshBannerResult> ProbeAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Opens a TCP connection and reads the SSH identification line, without sending any credentials.
/// This separates "SSH is not available" from "the login was refused".
/// </summary>
public sealed class TcpSshBannerProbe : ISshBannerProbe
{
    public async Task<SshBannerResult> ProbeAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await Compat.WithTimeout(tcp.ConnectAsync(address, port), timeout, cancellationToken, tcp.Close).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var buffer = new byte[1024];
            var total = 0;
            var deadline = DateTime.UtcNow + timeout;
            while (total < buffer.Length)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException();
                }

                var read = await Compat.WithTimeout(stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken), remaining, cancellationToken, tcp.Close)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                var identification = FindIdentification(Encoding.ASCII.GetString(buffer, 0, total));
                if (identification is not null)
                {
                    return new SshBannerResult(true, identification, null);
                }
            }

            return new SshBannerResult(false, null, "The device accepted a connection on the SSH port but did not identify itself as an SSH server.");
        }
        catch (TimeoutException)
        {
            return new SshBannerResult(false, null, $"No SSH answer within {timeout.TotalSeconds:0} seconds.");
        }
        catch (SocketException ex)
        {
            return new SshBannerResult(false, null, $"SSH connection failed: {ex.SocketErrorCode}.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return new SshBannerResult(false, null, $"SSH connection failed: {ex.Message}");
        }
    }

    private static string? FindIdentification(string text)
    {
        // RFC 4253 allows other lines before the identification string; only complete lines count.
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("SSH-", StringComparison.Ordinal))
            {
                return new string(line.Where(c => c >= 0x20 && c < 0x7F).Take(128).ToArray());
            }
        }

        return null;
    }
}
