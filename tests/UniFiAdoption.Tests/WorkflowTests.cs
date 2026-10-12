using System.Net;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Discovery;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Security;
using UniFiAdoption.Core.Ssh;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.Tests;

internal sealed class WorkflowTests
{
    private const string InformUrl = "http://unifi.oneacrefund.org:8080/inform";

    private sealed class Harness
    {
        public FakeAccessPoint Device { get; } = new();

        public List<FakeAccessPoint> OtherDevices { get; } = new();

        public AssistantSettings Settings { get; } = new() { VerifySeconds = 0, VerifyPollSeconds = 1 };

        public List<LocalInterfaceInfo> Interfaces { get; } = new() { FakeNetwork.Lan() };

        public bool AnswerDiscovery { get; set; } = true;

        public bool DiscoveryAccessDenied { get; set; }

        public bool DiscoveryHangs { get; set; }

        public bool BannerReachable { get; set; } = true;

        public bool ControllerReachableFromPc { get; set; } = true;

        public List<NeighborEntry> ExtraNeighbors { get; } = new();

        public string? DiscoverySourceOverride { get; set; }

        public JsonHostKeyStore HostKeys { get; } = new(Path.Combine(Path.GetTempPath(), $"hk-{Guid.NewGuid():N}.json"));

        public FakeSshConnector? Ssh { get; private set; }

        public DiagnosticLog Log { get; } = new(null);

        public Task<AdoptionReport> RunAsync(string suffix = "ABCD", bool testMode = false, SshCredential? session = null, CancellationToken ct = default)
        {
            var all = new[] { Device }.Concat(OtherDevices).ToArray();
            Ssh = new FakeSshConnector(all);
            var replies = AnswerDiscovery
                ? all.Select(d => UbntDiscoveryProtocol.TryParse(
                        DiscoveryProtocolTests.BuildReply(d.Mac.ToString(), DiscoverySourceOverride ?? d.Address.ToString()),
                        IPAddress.Parse(DiscoverySourceOverride ?? d.Address.ToString()))!)
                    .ToList()
                : new List<UbntDiscoveryReply>();
            var neighbors = DiscoverySourceOverride is null
                ? all.Select(d => new NeighborEntry(d.Address, d.Mac)).Concat(ExtraNeighbors).ToArray()
                : ExtraNeighbors.ToArray();

            var deps = new WorkflowDependencies(
                new FakeNetwork(Interfaces.ToArray()),
                new FakeDiscovery(replies, DiscoveryAccessDenied, DiscoveryHangs),
                new FakeNeighbors(neighbors),
                new FakeControllerProbe(ControllerReachableFromPc),
                new FakeBannerProbe(BannerReachable),
                Ssh,
                HostKeys,
                null);
            return new AdoptionWorkflow(Settings, deps, Log).RunAsync(new AdoptionRequest(suffix, testMode, session), null, ct);
        }
    }

    [Test]
    public async Task FactoryResetDeviceIsPointedAtControllerAndReportedAsPending()
    {
        var h = new Harness();
        var report = await h.RunAsync("abcd");

        Assert.Equal(AdoptionOutcome.PendingAdoption, report.Outcome);
        Assert.Equal(ResultTone.Waiting, report.Tone);
        Assert.True(report.DeviceWasChanged);
        Assert.True(h.Device.Commands.Contains($"mca-cli-op set-inform '{InformUrl}'"), "set-inform with the exact quoted URL");
        Assert.Equal(1, h.Device.Commands.Count(c => c.StartsWith("mca-cli-op set-inform", StringComparison.Ordinal)));
        Assert.False(h.Device.Commands.Any(c => c.IndexOf("/etc/hosts", StringComparison.Ordinal) >= 0 && c.IndexOf(">>", StringComparison.Ordinal) >= 0), "no hosts change when DNS works");
        Assert.Equal(InformUrl, h.Device.InformUrl);
        Assert.Equal("factory default login", report.Device!.LoginUsed);
        Assert.Contains("approves it", report.Message);
    }

    [Test]
    public async Task ConnectedStatusIsReportedAsSuccess()
    {
        var h = new Harness();
        h.Device.StatusAfterSetInform = "Connected";
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.Connected, report.Outcome);
        Assert.Equal(ResultTone.Success, report.Tone);
        Assert.True(report.DeviceWasChanged);
    }

    [Test]
    public async Task AlreadyConnectedDeviceIsLeftUnchanged()
    {
        var h = new Harness();
        h.Device.StatusState = "Connected";
        h.Device.InformUrl = InformUrl;
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.Connected, report.Outcome);
        Assert.False(report.DeviceWasChanged);
        Assert.False(h.Device.WasChanged);
        Assert.Equal("Already connected", report.Title);
    }

    [Test]
    public async Task TestModeNeverChangesTheDevice()
    {
        var h = new Harness();
        h.Device.DnsWorks = false;
        h.Settings.HostsFallbackControllerIp = "203.0.113.10";
        var report = await h.RunAsync(testMode: true);

        Assert.Equal(AdoptionOutcome.DryRunCompleted, report.Outcome);
        Assert.False(report.DeviceWasChanged);
        Assert.False(h.Device.WasChanged, "no write commands in test mode");
        Assert.True(report.PlannedCommands.Contains($"mca-cli-op set-inform '{InformUrl}'"));
        Assert.True(report.PlannedCommands.Any(c => c.IndexOf(">> /etc/hosts", StringComparison.Ordinal) >= 0));
    }

    [Test]
    public async Task InvalidInputStopsBeforeAnyNetworkActivity()
    {
        var h = new Harness();
        var report = await h.RunAsync("XYZ!");
        Assert.Equal(AdoptionOutcome.InvalidInput, report.Outcome);
        Assert.Equal(0, h.Ssh!.ConnectAttempts);
    }

    [Test]
    public async Task NoMatchingDevice()
    {
        var h = new Harness();
        var report = await h.RunAsync("1234");
        Assert.Equal(AdoptionOutcome.NotFound, report.Outcome);
        Assert.Equal(0, h.Ssh!.ConnectAttempts);
        Assert.True(report.NextSteps.Any(s => s.IndexOf("POE", StringComparison.Ordinal) >= 0));
    }

    [Test]
    public async Task MultipleMatchingDevicesAreNotTouched()
    {
        var h = new Harness();
        h.OtherDevices.Add(new FakeAccessPoint { Mac = MacAddress.Parse("F0:9F:C2:99:AB:CD"), Address = IPAddress.Parse("192.168.10.24") });
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.MultipleMatches, report.Outcome);
        Assert.Equal(0, h.Ssh!.ConnectAttempts);
        Assert.Contains("2 devices", report.Message);
    }

    [Test]
    public async Task NonUbiquitiDeviceReceivesNoCredentials()
    {
        var h = new Harness { AnswerDiscovery = false };
        h.Device.Mac = MacAddress.Parse("3C:52:82:12:AB:CD");
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.NotUnifiDevice, report.Outcome);
        Assert.Equal(0, h.Ssh!.ConnectAttempts);
    }

    [Test]
    public async Task ArpAloneIsEnoughForAUbiquitiOui()
    {
        var h = new Harness { AnswerDiscovery = false };
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.PendingAdoption, report.Outcome);
    }

    [Test]
    public async Task DeviceOnAnotherSubnetIsReportedNotTouched()
    {
        var h = new Harness { DiscoverySourceOverride = "192.168.1.20" };
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.OnOtherNetwork, report.Outcome);
        Assert.Contains("192.168.1.20", report.Message);
        Assert.Equal(0, h.Ssh!.ConnectAttempts);
    }

    [Test]
    public async Task NoNetworkAndSelfAssignedAddresses()
    {
        var none = new Harness();
        none.Interfaces.Clear();
        Assert.Equal(AdoptionOutcome.NoNetwork, (await none.RunAsync()).Outcome);

        var apipa = new Harness();
        apipa.Interfaces.Clear();
        apipa.Interfaces.Add(FakeNetwork.Lan("169.254.20.3", "255.255.0.0"));
        var report = await apipa.RunAsync();
        Assert.Equal(AdoptionOutcome.NoNetwork, report.Outcome);
        Assert.Contains("didn't get a network address", report.Message);
    }

    [Test]
    public async Task BlockedDiscoveryIsReportedAsPermissionProblem()
    {
        // Security software blocked the broadcast and nothing at all answered: say so, rather than "not found".
        var blocked = new Harness { AnswerDiscovery = false, DiscoveryAccessDenied = true, DiscoverySourceOverride = "10.99.0.1" };
        var report = await blocked.RunAsync();
        Assert.Equal(AdoptionOutcome.PermissionDenied, report.Outcome);
        Assert.Equal(0, blocked.Ssh!.ConnectAttempts);

        // If other devices were seen, the search itself worked and it is an ordinary miss.
        var partial = new Harness { DiscoveryAccessDenied = true };
        Assert.Equal(AdoptionOutcome.NotFound, (await partial.RunAsync("FFFF")).Outcome);
    }

    [Test]
    public async Task SshUnavailable()
    {
        var h = new Harness { BannerReachable = false };
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.SshUnavailable, report.Outcome);
        Assert.Equal(0, h.Ssh!.ConnectAttempts);
        Assert.False(report.DeviceWasChanged);
    }

    [Test]
    public async Task RefusedFactoryPasswordMeansAlreadyAdoptedAndNothingChanges()
    {
        var h = new Harness();
        h.Device.Password = "set-by-the-controller";
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.AlreadyAdopted, report.Outcome);
        Assert.Equal(ResultTone.Success, report.Tone);
        Assert.False(report.DeviceWasChanged);
        Assert.Equal(1, h.Device.LoginAttempts.Count);
        Assert.Equal(CredentialSource.FactoryDefault, h.Device.LoginAttempts[0].Source);
    }

    [Test]
    public async Task ItProvidedLoginIsTriedFirstAndNeverLogged()
    {
        var h = new Harness();
        h.Device.Username = "siteadmin";
        h.Device.Password = "Sup3r-Secret-Value";
        var report = await h.RunAsync(session: new SshCredential("siteadmin", "Sup3r-Secret-Value", CredentialSource.ItProvidedThisSession));
        Assert.Equal(AdoptionOutcome.PendingAdoption, report.Outcome);
        Assert.Equal(1, h.Device.LoginAttempts.Count);
        Assert.DoesNotContain("Sup3r-Secret-Value", string.Join("\n", h.Log.Snapshot()));
        Assert.DoesNotContain("Sup3r-Secret-Value", report.ToSupportText(null));
    }

    [Test]
    public async Task NoLoginAllowedMeansNoAttempt()
    {
        var h = new Harness();
        h.Settings.AllowFactoryDefaultCredentials = false;
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.AuthenticationFailed, report.Outcome);
        Assert.Equal(0, h.Ssh!.ConnectAttempts);
    }

    [Test]
    public async Task HostKeyChangeIsAcceptedForTheFactoryLoginAfterAReset()
    {
        var h = new Harness();
        h.HostKeys.Remember(h.Device.Mac, h.Device.HostKeyAlgorithm, "AnEarlierDifferentFingerprint");
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.PendingAdoption, report.Outcome);
        Assert.Equal(HostKeyCheck.TrustedKnown, h.HostKeys.Check(h.Device.Mac, h.Device.HostKeyAlgorithm, h.Device.HostKeyFingerprint), "new key re-recorded");
    }

    [Test]
    public async Task HostKeyChangeIsRefusedWhenAnItLoginWouldBeSent()
    {
        var h = new Harness();
        h.Device.Username = "siteadmin";
        h.Device.Password = "Sup3r-Secret-Value";
        h.HostKeys.Remember(h.Device.Mac, h.Device.HostKeyAlgorithm, "AnEarlierDifferentFingerprint");
        var report = await h.RunAsync(session: new SshCredential("siteadmin", "Sup3r-Secret-Value", CredentialSource.ItProvidedThisSession));
        Assert.Equal(AdoptionOutcome.HostKeyMismatch, report.Outcome);
        Assert.False(h.Device.WasChanged);
        Assert.Equal(0, h.Device.LoginAttempts.Count, "the password was never sent");
    }

    [Test]
    public async Task HostKeyIsRecordedAfterIdentityIsConfirmed()
    {
        var h = new Harness();
        await h.RunAsync();
        Assert.Equal(HostKeyCheck.TrustedKnown, h.HostKeys.Check(h.Device.Mac, h.Device.HostKeyAlgorithm, h.Device.HostKeyFingerprint));
    }

    [Test]
    public async Task DeviceThatReportsADifferentMacIsNotChanged()
    {
        var h = new Harness();
        h.Device.ReportedMacOverride = MacAddress.Parse("80:2A:A8:00:00:01");
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.DeviceIdentityMismatch, report.Outcome);
        Assert.False(h.Device.WasChanged);
        Assert.Equal(HostKeyCheck.TrustedFirstUse, h.HostKeys.Check(h.Device.Mac, h.Device.HostKeyAlgorithm, h.Device.HostKeyFingerprint));
    }

    [Test]
    public async Task UnsupportedFirmwareWithoutCliIsNotChanged()
    {
        var h = new Harness();
        h.Device.HasMcaCliOp = false;
        h.Device.HasMcaCli = false;
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.UnsupportedDevice, report.Outcome);
        Assert.False(h.Device.WasChanged);
    }

    [Test]
    public async Task UnsupportedModelIsNotChanged()
    {
        var h = new Harness();
        h.Device.Model = "USW-24-POE";
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.UnsupportedDevice, report.Outcome);
        Assert.False(h.Device.WasChanged);
    }

    [Test]
    public async Task LegacyInteractiveFallbackIsUsedOnlyWithoutMcaCliOp()
    {
        var h = new Harness();
        h.Device.HasMcaCliOp = false;
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.InformSentNotVerified, report.Outcome);
        Assert.True(h.Device.Commands.Contains("mca-cli"));
        Assert.True(h.Device.Commands.Contains($"set-inform {InformUrl}"));
        Assert.Equal(InformUrl, h.Device.InformUrl);

        var disabled = new Harness();
        disabled.Device.HasMcaCliOp = false;
        disabled.Settings.AllowInteractiveMcaCliFallback = false;
        Assert.Equal(AdoptionOutcome.UnsupportedDevice, (await disabled.RunAsync()).Outcome);
        Assert.False(disabled.Device.WasChanged);
    }

    [Test]
    public async Task ControllerNameNotResolvableOnTheDevice()
    {
        var h = new Harness { ControllerReachableFromPc = false };
        h.Device.DnsWorks = false;
        h.Device.StatusAfterSetInform = "Timeout";
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.ControllerNameUnresolvable, report.Outcome);
        Assert.False(h.Device.Commands.Any(c => c.IndexOf(">> /etc/hosts", StringComparison.Ordinal) >= 0), "hosts untouched without a configured fallback IP");
        Assert.Contains("can't reach the controller either", report.Message);
    }

    [Test]
    public async Task HostsFallbackAddsOneCorrectLineAndNeverDuplicates()
    {
        var h = new Harness();
        h.Device.DnsWorks = false;
        h.Device.HostsContent = "127.0.0.1 localhost\nunifi.oneacrefund.org unifi\n";
        h.Settings.HostsFallbackControllerIp = "203.0.113.10";

        var first = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.PendingAdoption, first.Outcome);
        Assert.True(first.TechnicalNotes.Any(n => n.IndexOf("malformed", StringComparison.Ordinal) >= 0));

        await h.RunAsync();
        await h.RunAsync();
        var correctLines = h.Device.HostsContent.Split('\n').Count(l => l == "203.0.113.10 unifi.oneacrefund.org");
        Assert.Equal(1, correctLines);
        Assert.Contains("unifi.oneacrefund.org unifi", h.Device.HostsContent); // the old line is left alone
    }

    [Test]
    public async Task ConflictingHostsEntryIsNotOverwritten()
    {
        var h = new Harness();
        h.Device.DnsWorks = false;
        h.Device.HostsContent = "10.0.0.9 unifi.oneacrefund.org\n";
        h.Settings.HostsFallbackControllerIp = "203.0.113.10";
        var report = await h.RunAsync(testMode: true);
        Assert.False(report.PlannedCommands.Any(c => c.IndexOf("/etc/hosts", StringComparison.Ordinal) >= 0));
    }

    [Test]
    public async Task ControllerUnreachableFromTheDevice()
    {
        var h = new Harness();
        h.Device.StatusAfterSetInform = "Timeout";
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.ControllerUnreachable, report.Outcome);
        Assert.True(report.DeviceWasChanged);
    }

    [Test]
    public async Task ControllerRejection()
    {
        var h = new Harness();
        h.Device.StatusAfterSetInform = "Server Reject";
        Assert.Equal(AdoptionOutcome.ControllerRejected, (await h.RunAsync()).Outcome);
    }

    [Test]
    public async Task InformCommandFailure()
    {
        var h = new Harness();
        h.Device.SetInformFails = true;
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.InformFailed, report.Outcome);
    }

    [Test]
    public async Task InvalidSettingsStopEverything()
    {
        var h = new Harness();
        h.Settings.InformUrl = "http://unifi.oneacrefund.org:8080/inform;reboot";
        var report = await h.RunAsync();
        Assert.Equal(AdoptionOutcome.ConfigurationError, report.Outcome);
        Assert.Equal(0, h.Ssh!.ConnectAttempts);
    }

    [Test]
    public async Task CancellationDuringDiscoveryChangesNothing()
    {
        var h = new Harness { DiscoveryHangs = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var report = await h.RunAsync(ct: cts.Token);
        Assert.Equal(AdoptionOutcome.Cancelled, report.Outcome);
        Assert.False(report.DeviceWasChanged);
        Assert.Contains("Nothing was changed", report.Message);
    }

    [Test]
    public async Task VerificationPollsUntilConnected()
    {
        var h = new Harness();
        h.Settings.VerifySeconds = 3;
        h.Settings.VerifyPollSeconds = 1;
        h.Device.StatusAfterSetInform = "Not Adopted";
        var run = h.RunAsync();
        await Task.Delay(1300);
        h.Device.StatusState = "Connected";
        var report = await run;
        Assert.Equal(AdoptionOutcome.Connected, report.Outcome);
    }
}
