using System.Net;
using System.Text.RegularExpressions;
using UniFiAdoption.Core.Device;
using UniFiAdoption.Core.Discovery;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Ssh;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.Tests;

/// <summary>
/// In-memory stand-in for an access point's SSH shell. It answers the exact commands the workflow
/// sends, with output shaped like UniFi firmware output, and records every command.
/// </summary>
internal sealed class FakeAccessPoint
{
    public MacAddress Mac { get; set; } = MacAddress.Parse("80:2A:A8:12:AB:CD");

    public IPAddress Address { get; set; } = IPAddress.Parse("192.168.10.23");

    public string Username { get; set; } = "ubnt";

    public string Password { get; set; } = "ubnt";

    public bool SshAvailable { get; set; } = true;

    public bool HasMcaCliOp { get; set; } = true;

    public bool HasMcaCli { get; set; } = true;

    public string Model { get; set; } = "UAP-AC-Pro-Gen2";

    public string Version { get; set; } = "6.6.77.15402";

    public string StatusState { get; set; } = "Not Adopted";

    public string InformUrl { get; set; } = "http://unifi:8080/inform";

    public string StatusAfterSetInform { get; set; } = "Not Adopted";

    public bool SetInformFails { get; set; }

    public bool DnsWorks { get; set; } = true;

    public string ControllerIp { get; set; } = "203.0.113.10";

    public string HostsContent { get; set; } = "127.0.0.1 localhost\n";

    public MacAddress? ReportedMacOverride { get; set; }

    public string HostKeyAlgorithm { get; set; } = "ssh-ed25519";

    public string HostKeyFingerprint { get; set; } = "Q2hlY2tUaGlzRmFrZUtleUZpbmdlcnByaW50";

    public List<string> Commands { get; } = new();

    /// <summary>Optional delay per command, to observe concurrency in batch tests.</summary>
    public TimeSpan CommandDelay { get; set; }

    /// <summary>Fail this many SSH connections with "unavailable" before succeeding (retry tests).</summary>
    public int FailConnectionsRemaining { get; set; }

    public List<SshCredential> LoginAttempts { get; } = new();

    public bool WasChanged => Commands.Any(c => c.StartsWith("mca-cli-op set-inform", StringComparison.Ordinal) || c.IndexOf(">> /etc/hosts", StringComparison.Ordinal) >= 0 || c.StartsWith("set-inform", StringComparison.Ordinal));

    public CommandResult Execute(string command)
    {
        Commands.Add(command);
        if (command == DeviceCommands.FindMcaCliOp)
        {
            return HasMcaCliOp ? Ok(command, "/usr/bin/mca-cli-op\n") : Fail(command, 1, "");
        }

        if (command == DeviceCommands.FindMcaCli)
        {
            return HasMcaCli ? Ok(command, "/usr/bin/mca-cli\n") : Fail(command, 1, "");
        }

        if (command == DeviceCommands.Info)
        {
            return HasMcaCliOp
                ? Ok(command, $"""
                    Model:       {Model}
                    Version:     {Version}
                    MAC Address: {(ReportedMacOverride ?? Mac).ToString().ToLowerInvariant()}
                    IP Address:  {Address}
                    Hostname:    UBNT
                    Uptime:      812 seconds

                    Status:      {StatusState} ({InformUrl})
                    """)
                : Fail(command, 127, "sh: mca-cli-op: not found");
        }

        if (command == DeviceCommands.ReadMacAddresses)
        {
            var mac = (ReportedMacOverride ?? Mac).ToString().ToLowerInvariant();
            return Ok(command, $"{mac}\n{mac}\n");
        }

        if (command == DeviceCommands.ReadHostsFile)
        {
            return Ok(command, HostsContent);
        }

        if (command.StartsWith("ping -c 1 -W 2 ", StringComparison.Ordinal))
        {
            var host = Regex.Match(command, "'([^']+)'").Groups[1].Value;
            var fromHosts = HostsFile.FindEntriesFor(HostsContent, host).FirstOrDefault(e => !e.IsMalformed);
            if (fromHosts is not null)
            {
                return Fail(command, 1, $"PING {host} ({fromHosts.Address}): 56 data bytes\n\n--- {host} ping statistics ---\n1 packets transmitted, 0 packets received, 100% packet loss\n");
            }

            return DnsWorks
                ? Fail(command, 1, $"PING {host} ({ControllerIp}): 56 data bytes\n\n--- {host} ping statistics ---\n1 packets transmitted, 0 packets received, 100% packet loss\n")
                : Fail(command, 1, $"ping: bad address '{host}'\n");
        }

        var hostsAppend = Regex.Match(command, @"^printf '%s\\n' '(?<line>[^']+)' >> /etc/hosts$");
        if (hostsAppend.Success)
        {
            HostsContent += hostsAppend.Groups["line"].Value + "\n";
            return Ok(command, "");
        }

        var setInform = Regex.Match(command, @"^mca-cli-op set-inform '(?<url>[^']+)'$");
        if (setInform.Success && HasMcaCliOp)
        {
            if (SetInformFails)
            {
                return Fail(command, 1, "Error: could not set inform URL\n");
            }

            InformUrl = setInform.Groups["url"].Value;
            StatusState = StatusAfterSetInform;
            return Ok(command, $"Adoption request sent to '{InformUrl}'.  Use the controller to complete the adopt process.\n");
        }

        return Fail(command, 127, $"sh: {command.Split(' ')[0]}: not found");
    }

    public InteractiveResult ExecuteInteractive(IReadOnlyList<InteractiveStep> steps)
    {
        var transcript = "BZ.v4.3.28# ";
        var completed = 0;
        foreach (var step in steps)
        {
            Commands.Add(step.Send);
            if (step.Send == "mca-cli")
            {
                if (!HasMcaCli)
                {
                    return new InteractiveResult(false, transcript + "sh: mca-cli: not found", completed);
                }

                transcript += "mca-cli\r\nUBNT# ";
            }
            else if (step.Send.StartsWith("set-inform ", StringComparison.Ordinal))
            {
                InformUrl = step.Send.Substring("set-inform ".Length);
                StatusState = StatusAfterSetInform;
                transcript += $"{step.Send}\r\nAdoption request sent to '{InformUrl}'.  Use the controller to complete the adopt process.\r\nUBNT# ";
            }
            else
            {
                transcript += $"{step.Send}\r\nBZ.v4.3.28# ";
            }

            completed++;
        }

        return new InteractiveResult(true, transcript, completed);
    }

    private static CommandResult Ok(string command, string output) => new(command, 0, output, "", false);

    private static CommandResult Fail(string command, int code, string output) => new(command, code, output, "", false);
}

internal sealed class FakeSshConnector(params FakeAccessPoint[] devices) : ISshConnector
{
    public int ConnectAttempts { get; private set; }

    public Task<ISshSession> ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken)
    {
        lock (this)
        {
            ConnectAttempts++;
        }

        var device = devices.FirstOrDefault(d => d.Address.Equals(request.Address))
                     ?? throw new SshUnavailableException("No device at that address.");
        if (!device.SshAvailable)
        {
            throw new SshUnavailableException("Connection refused.");
        }

        if (device.FailConnectionsRemaining > 0)
        {
            device.FailConnectionsRemaining--;
            throw new SshUnavailableException("Connection reset.");
        }

        if (request.VerifyHostKey(device.HostKeyAlgorithm, device.HostKeyFingerprint) == HostKeyCheck.Rejected)
        {
            throw new SshHostKeyRejectedException(device.HostKeyAlgorithm, device.HostKeyFingerprint);
        }

        lock (device)
        {
            device.LoginAttempts.Add(request.Credential);
        }

        if (request.Credential.Username != device.Username || request.Credential.Password != device.Password)
        {
            throw new SshAuthenticationFailedException("Permission denied.");
        }

        return Task.FromResult<ISshSession>(new FakeSession(device));
    }

    internal sealed class FakeSession(FakeAccessPoint device) : ISshSession
    {
        public string? HostKeyAlgorithm => device.HostKeyAlgorithm;

        public string? HostKeyFingerprint => device.HostKeyFingerprint;

        public static int Concurrent;

        public static int MaxConcurrent;

        public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Interlocked.Increment(ref Concurrent);
            int seen;
            while (now > (seen = MaxConcurrent) && Interlocked.CompareExchange(ref MaxConcurrent, now, seen) != seen)
            {
            }

            try
            {
                if (device.CommandDelay > TimeSpan.Zero)
                {
                    await Task.Delay(device.CommandDelay, cancellationToken);
                }

                lock (device)
                {
                    return device.Execute(command);
                }
            }
            finally
            {
                Interlocked.Decrement(ref Concurrent);
            }
        }

        public Task<InteractiveResult> RunInteractiveAsync(IReadOnlyList<InteractiveStep> steps, TimeSpan stepTimeout, CancellationToken cancellationToken) =>
            Task.FromResult(device.ExecuteInteractive(steps));

        public void Dispose()
        {
        }
    }
}

internal sealed class FakeNetwork(params LocalInterfaceInfo[] interfaces) : ILocalNetworkProvider
{
    public static LocalInterfaceInfo Lan(string address = "192.168.10.5", string mask = "255.255.255.0") =>
        new("Ethernet", "Intel Ethernet", IPAddress.Parse(address),
            Ipv4Subnet.FromAddressAndMask(IPAddress.Parse(address), IPAddress.Parse(mask)), IPAddress.Parse("192.168.10.1"), false, false);

    public IReadOnlyList<LocalInterfaceInfo> GetIPv4Interfaces() => interfaces;
}

internal sealed class FakeDiscovery(IReadOnlyList<UbntDiscoveryReply> replies, bool accessDenied = false, bool hang = false) : IUbntDiscovery
{
    public async Task<UbntDiscoveryResult> DiscoverAsync(IReadOnlyList<LocalInterfaceInfo> interfaces, TimeSpan listenTime, CancellationToken cancellationToken)
    {
        if (hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return new UbntDiscoveryResult(replies, accessDenied);
    }
}

internal sealed class FakeNeighbors(params NeighborEntry[] entries) : INeighborScanner
{
    public Task<NeighborScanResult> ScanAsync(IReadOnlyList<LocalInterfaceInfo> interfaces, int maxHostsPerInterface, int parallelism,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(1.0);
        return Task.FromResult(new NeighborScanResult(entries.ToList(), 254, false));
    }
}

internal sealed class FakeControllerProbe(bool reachable = true) : IControllerProbe
{
    public Task<ControllerReachability> ProbeAsync(ControllerTarget controller, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(reachable
            ? new ControllerReachability(true, [IPAddress.Parse("203.0.113.10")], true, null)
            : new ControllerReachability(false, [], false, "The controller name could not be resolved (HostNotFound)."));
}

internal sealed class FakeBannerProbe(bool reachable = true) : ISshBannerProbe
{
    public Task<SshBannerResult> ProbeAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(reachable ? new SshBannerResult(true, "SSH-2.0-dropbear_2019.78", null) : new SshBannerResult(false, null, "SSH connection failed: ConnectionRefused."));
}
