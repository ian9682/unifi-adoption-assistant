using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await tcp.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var buffer = new byte[1024];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SshBannerResult(false, null, $"No SSH answer within {timeout.TotalSeconds:0} seconds.");
        }
        catch (SocketException ex)
        {
            return new SshBannerResult(false, null, $"SSH connection failed: {ex.SocketErrorCode}.");
        }
        catch (IOException ex)
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

/// <summary>
/// SSH via SSH.NET. Chosen over the Windows OpenSSH client because it can authenticate with a
/// password without a terminal, exposes the host key for verification, and still supports the
/// older key-exchange and host-key algorithms used by older UniFi firmware.
/// </summary>
public sealed class SshNetConnector : ISshConnector
{
    public async Task<ISshSession> ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken)
    {
        var credential = request.Credential;
        var passwordMethod = new PasswordAuthenticationMethod(credential.Username, credential.Password);
        var keyboardMethod = new KeyboardInteractiveAuthenticationMethod(credential.Username);
        keyboardMethod.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    prompt.Response = credential.Password;
                }
            }
        };

        var connectionInfo = new ConnectionInfo(
            request.Address.ToString(), request.Port, credential.Username, passwordMethod, keyboardMethod)
        {
            Timeout = request.ConnectTimeout,
        };

        var client = new SshClient(connectionInfo);
        HostKeyCheck? decision = null;
        string? algorithm = null;
        string? fingerprint = null;

        client.HostKeyReceived += (_, e) =>
        {
            algorithm = e.HostKeyName;
            fingerprint = e.FingerPrintSHA256;
            decision = request.VerifyHostKey(algorithm, fingerprint);
            e.CanTrust = decision != HostKeyCheck.Rejected;
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(request.ConnectTimeout + request.ConnectTimeout);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();

            if (decision == HostKeyCheck.Rejected)
            {
                throw new SshHostKeyRejectedException(algorithm, fingerprint, ex);
            }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw ex switch
            {
                SshAuthenticationException => new SshAuthenticationFailedException(
                    $"The device refused the {credential.SourceDescription}.", ex),
                OperationCanceledException or SshOperationTimeoutException =>
                    new SshUnavailableException("The SSH connection timed out.", ex),
                SocketException or SshConnectionException or ProxyException or IOException or SshException =>
                    new SshUnavailableException($"The SSH connection failed: {ex.Message}", ex),
                _ => ex,
            };
        }

        return new SshNetSession(client, algorithm, fingerprint);
    }
}

internal sealed class SshNetSession(SshClient client, string? hostKeyAlgorithm, string? hostKeyFingerprint) : ISshSession
{
    // A login shell or mca-cli prompt ends in '#', '$' or '>' followed by optional spaces.
    private static readonly Regex PromptPattern = new(@"[#$>]\s*$", RegexOptions.CultureInvariant);

    public string? HostKeyAlgorithm { get; } = hostKeyAlgorithm;

    public string? HostKeyFingerprint { get; } = hostKeyFingerprint;

    public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = timeout;
        try
        {
            await sshCommand.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new CommandResult(command, sshCommand.ExitStatus, sshCommand.Result, sshCommand.Error, false);
        }
        catch (SshOperationTimeoutException)
        {
            return new CommandResult(command, null, SafeRead(() => sshCommand.Result), SafeRead(() => sshCommand.Error), true);
        }
        catch (SshConnectionException ex)
        {
            throw new SshUnavailableException("The connection to the access point was lost.", ex);
        }
    }

    public Task<InteractiveResult> RunInteractiveAsync(
        IReadOnlyList<InteractiveStep> steps, TimeSpan stepTimeout, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var shell = client.CreateShellStream("vt100", 160, 40, 0, 0, 16 * 1024);
                using var registration = cancellationToken.Register(() =>
                {
                    try
                    {
                        shell.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });

                var transcript = new StringBuilder();
                var initial = shell.Expect(PromptPattern, stepTimeout);
                if (initial is null)
                {
                    return new InteractiveResult(false, transcript.ToString(), 0);
                }

                transcript.Append(initial);
                var completed = 0;
                foreach (var step in steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    shell.WriteLine(step.Send);
                    var output = shell.Expect(step.Expect, stepTimeout);
                    if (output is null)
                    {
                        return new InteractiveResult(false, transcript.ToString(), completed);
                    }

                    transcript.Append(output);
                    completed++;
                }

                return new InteractiveResult(true, transcript.ToString(), completed);
            },
            cancellationToken);

    public ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // The connection is being torn down anyway.
        }
        finally
        {
            client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static string SafeRead(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or SshException)
        {
            return string.Empty;
        }
    }
}
