using System.Net;
using System.Net.Sockets;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Discovery;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Security;
using UniFiAdoption.Core.Ssh;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.Tests;

/// <summary>
/// End-to-end tests with the real SSH.NET connector, real UDP discovery and real TCP probes, against
/// a simulated access point on 127.0.0.1 (see docs/TEST-PLAN.md, "Simulated device"). They exercise
/// everything except the Windows ARP sweep and the window. They do NOT prove behaviour on real
/// UniFi firmware; that needs the lab checks in the test plan.
///
/// Environment variables:
///   UNIFI_IT_SSH_PORT  SSH port of the simulated device (e.g. 2222)
///   UNIFI_IT_STATE     state directory of the simulated device (e.g. /tmp/fake-unifi)
///   UNIFI_IT_INFORM    inform URL whose host the device can resolve (e.g. http://controller.test:8080/inform)
/// </summary>
internal sealed class IntegrationTests
{
    private const string Mac = "80:2A:A8:12:AB:CD";

    private static int Port => int.Parse(Environment.GetEnvironmentVariable("UNIFI_IT_SSH_PORT") ?? "2222");

    private static string StateDir => Environment.GetEnvironmentVariable("UNIFI_IT_STATE") ?? "/tmp/fake-unifi";

    private static string InformUrl => Environment.GetEnvironmentVariable("UNIFI_IT_INFORM") ?? "http://controller.test:8080/inform";

    private static string DeviceInform => File.ReadAllText(Path.Combine(StateDir, "inform"));

    private static void ResetDevice()
    {
        File.WriteAllText(Path.Combine(StateDir, "inform"), "http://unifi:8080/inform");
        File.WriteAllText(Path.Combine(StateDir, "state"), "Not Adopted");
    }

    private static async Task<(AdoptionReport Report, DiagnosticLog Log)> RunAsync(
        bool testMode = false, Action<AssistantSettings>? configure = null, JsonHostKeyStore? hostKeys = null, string mac = Mac)
    {
        var settings = new AssistantSettings { InformUrl = InformUrl, SshPort = Port, VerifySeconds = 2, VerifyPollSeconds = 1, DiscoveryListenSeconds = 2 };
        configure?.Invoke(settings);
        settings.Normalize();

        using var responder = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var discoveryPort = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        using var cts = new CancellationTokenSource();
        var serve = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var request = await responder.ReceiveAsync(cts.Token);
                    await responder.SendAsync(DiscoveryProtocolTests.BuildReply(mac, "127.0.0.1"), request.RemoteEndPoint, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var loopback = new LocalInterfaceInfo("lo", "loopback", IPAddress.Loopback,
            Ipv4Subnet.FromAddressAndMask(IPAddress.Loopback, IPAddress.Parse("255.255.255.0")), null, false, false);
        var log = new DiagnosticLog(null);
        var deps = new WorkflowDependencies(
            new FakeNetwork(loopback),
            new UbntDiscoveryScanner(discoveryPort),
            new NullNeighborScanner(),
            new NetworkControllerProbe(),
            new TcpSshBannerProbe(),
            new SshNetConnector(),
            hostKeys ?? new JsonHostKeyStore(Path.Combine(Path.GetTempPath(), $"it-hk-{Guid.NewGuid():N}.json")),
            null);
        var report = await new AdoptionWorkflow(settings, deps, log).RunAsync(new AdoptionRequest(MacAddress.Parse(mac).Hex[^4..], testMode, null), null, CancellationToken.None);
        cts.Cancel();
        await serve;
        return (report, log);
    }

    [Test("integration")]
    public async Task TestModeLogsInButChangesNothing()
    {
        ResetDevice();
        var (report, log) = await RunAsync(testMode: true);
        Assert.Equal(AdoptionOutcome.DryRunCompleted, report.Outcome, string.Join("\n", log.Snapshot()));
        Assert.Equal("http://unifi:8080/inform", DeviceInform);
        Assert.Equal($"mca-cli-op set-inform '{InformUrl}'", report.PlannedCommands.Single());
        Assert.Equal("UAP-AC-Pro-Gen2", report.Device!.Model);
    }

    [Test("integration")]
    public async Task RealRunSetsInformAndReportsPending()
    {
        ResetDevice();
        var (report, log) = await RunAsync();
        Assert.Equal(AdoptionOutcome.PendingAdoption, report.Outcome, string.Join("\n", log.Snapshot()));
        Assert.Equal(InformUrl, DeviceInform);
        Assert.True(report.DeviceWasChanged);
    }

    [Test("integration")]
    public async Task HostKeyIsRememberedAndChecked()
    {
        ResetDevice();
        var store = new JsonHostKeyStore(Path.Combine(Path.GetTempPath(), $"it-hk-{Guid.NewGuid():N}.json"));
        var (_, firstLog) = await RunAsync(testMode: true, hostKeys: store);
        Assert.Contains("TrustedFirstUse", string.Join("\n", firstLog.Snapshot()));
        var (second, secondLog) = await RunAsync(testMode: true, hostKeys: store);
        Assert.Equal(AdoptionOutcome.DryRunCompleted, second.Outcome);
        Assert.Contains("TrustedKnown", string.Join("\n", secondLog.Snapshot()));

        var pinnedWrong = new JsonHostKeyStore(Path.Combine(Path.GetTempPath(), $"it-hk-{Guid.NewGuid():N}.json"),
            new Dictionary<string, string> { [Mac] = "SHA256:ThisIsNotTheRightFingerprint" });
        var (mismatch, _) = await RunAsync(hostKeys: pinnedWrong);
        Assert.Equal(AdoptionOutcome.HostKeyMismatch, mismatch.Outcome);
        Assert.Equal("http://unifi:8080/inform", DeviceInform);
    }

    [Test("integration")]
    public async Task WrongPasswordIsReportedAsAuthenticationFailure()
    {
        ResetDevice();
        var (report, _) = await RunAsync(configure: s => s.FactoryDefaultPassword = "not-the-password");
        Assert.Equal(AdoptionOutcome.AuthenticationFailed, report.Outcome);
        Assert.Equal("http://unifi:8080/inform", DeviceInform);
    }

    [Test("integration")]
    public async Task WrongPortIsReportedAsSshUnavailable()
    {
        ResetDevice();
        var (report, _) = await RunAsync(configure: s => s.SshPort = 1);
        Assert.Equal(AdoptionOutcome.SshUnavailable, report.Outcome);
    }

    [Test("integration")]
    public async Task UnresolvableControllerNameIsDetectedOnTheDevice()
    {
        ResetDevice();
        var (report, log) = await RunAsync(configure: s => s.InformUrl = "http://no-such-controller.invalid:8080/inform");
        Assert.Equal(AdoptionOutcome.ControllerNameUnresolvable, report.Outcome, string.Join("\n", log.Snapshot()));
        Assert.Equal("http://no-such-controller.invalid:8080/inform", DeviceInform);
    }

    [Test("integration")]
    public async Task InteractiveFallbackDrivesAPseudoTerminal()
    {
        const string tool = "/usr/bin/mca-cli-op";
        const string hidden = "/usr/bin/mca-cli-op.hidden-for-test";
        ResetDevice();
        // Without mca-cli-op the device's identity is read from /sys/class/net, so use this host's real MAC.
        var realMac = File.ReadAllText("/sys/class/net/eth0/address").Trim();
        File.Move(tool, hidden);
        try
        {
            var (report, log) = await RunAsync(mac: realMac);
            Assert.Equal(AdoptionOutcome.InformSentNotVerified, report.Outcome, string.Join("\n", log.Snapshot()));
            Assert.Equal(InformUrl, DeviceInform);
        }
        finally
        {
            File.Move(hidden, tool);
        }
    }
}
