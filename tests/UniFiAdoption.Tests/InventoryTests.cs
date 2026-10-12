using System.Net;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Device;
using UniFiAdoption.Core.Discovery;
using UniFiAdoption.Core.Inventory;
using UniFiAdoption.Core.Json;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Security;
using UniFiAdoption.Core.Ssh;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.Tests;

internal sealed class ClassifierTests
{
    private static readonly AssistantSettings Settings = new();
    private static readonly ControllerTarget Controller = Target();

    private static ControllerTarget Target()
    {
        ControllerTarget.TryCreate("http://unifi.oneacrefund.org:8080/inform", out var t, out _);
        return t!;
    }

    private static DeviceRecord Record(Action<DeviceRecord> setup)
    {
        var r = new DeviceRecord(MacAddress.Parse("802AA812ABCD"), DateTime.Now) { ConfirmedUbiquiti = true, LastChecked = DateTime.Now };
        setup(r);
        return r;
    }

    private static DeviceInfo Info(string status, string url, string model = "UAP-AC-Pro-Gen2") =>
        DeviceInfoParser.Parse($"Model: {model}\nMAC Address: 80:2a:a8:12:ab:cd\nStatus: {status} ({url})");

    private static DeviceStatus Status(DeviceRecord r) => Classifier.Classify(r, DateTime.Now, Settings, Controller).Status;

    [Test]
    public void FactoryDeviceIsReady() =>
        Assert.Equal(DeviceStatus.Ready, Status(Record(r => { r.SshOpen = true; r.Login = LoginResult.FactoryAccepted; r.Info = Info("Not Adopted", "http://unifi:8080/inform"); })));

    [Test]
    public void PointedAtOurControllerIsWaitingForApproval() =>
        Assert.Equal(DeviceStatus.WaitingForApproval, Status(Record(r => { r.SshOpen = true; r.Login = LoginResult.FactoryAccepted; r.Info = Info("Not Adopted", "http://unifi.oneacrefund.org:8080/inform"); })));

    [Test]
    public void TeamRuleFactoryRefusedWithSshOpenIsAdopted() =>
        Assert.Equal(DeviceStatus.Adopted, Status(Record(r => { r.SshOpen = true; r.Login = LoginResult.FactoryRefused; })));

    [Test]
    public void ConnectedToOurControllerIsAdopted() =>
        Assert.Equal(DeviceStatus.Adopted, Status(Record(r => { r.SshOpen = true; r.Login = LoginResult.FactoryAccepted; r.Info = Info("Connected", "http://unifi.oneacrefund.org:8080/inform"); })));

    [Test]
    public void ClosedSshNeedsAttentionUnlessWeJustSentIt()
    {
        Assert.Equal(DeviceStatus.NeedsAttention, Status(Record(r => r.SshOpen = false)));
        Assert.Equal(DeviceStatus.WaitingForApproval, Status(Record(r => { r.SshOpen = false; r.InformSentAt = DateTime.Now.AddMinutes(-2); })));
    }

    [Test]
    public void NotSeenForAWhileIsNotRespondingUnlessBeingApproved()
    {
        Assert.Equal(DeviceStatus.NotResponding, Status(Record(r => { r.SshOpen = true; r.Login = LoginResult.FactoryAccepted; r.LastSeen = DateTime.Now.AddMinutes(-20); })));
        Assert.Equal(DeviceStatus.WaitingForApproval, Status(Record(r => { r.LastSeen = DateTime.Now.AddMinutes(-20); r.InformSentAt = DateTime.Now.AddMinutes(-5); })));
    }

    [Test]
    public void UnsupportedModelNeedsAttention() =>
        Assert.Equal(DeviceStatus.NeedsAttention, Status(Record(r => { r.SshOpen = true; r.Login = LoginResult.FactoryAccepted; r.Info = Info("Not Adopted", "http://unifi:8080/inform", "USW-24-POE"); })));

    [Test]
    public void NotYetCheckedIsChecking() =>
        Assert.Equal(DeviceStatus.Checking, Status(Record(r => r.LastChecked = null)));

    [Test]
    public void JobStatesOverrideAndFailuresClearAfterARecheck()
    {
        Assert.Equal(DeviceStatus.Working, Status(Record(r => r.Job = JobState.Running)));
        var failed = Record(r => { r.SshOpen = true; r.Login = LoginResult.FactoryAccepted; r.Job = JobState.Failed; r.JobFinishedAt = DateTime.Now; r.LastChecked = DateTime.Now.AddMinutes(-1); });
        Assert.Equal(DeviceStatus.NeedsAttention, Status(failed));
        failed.LastChecked = DateTime.Now.AddSeconds(1);
        Assert.Equal(DeviceStatus.Ready, Status(failed));
    }

    [Test]
    public void AdoptedDevicesAreNeverRecheckedAutomatically()
    {
        var adopted = Record(r => { r.SshOpen = true; r.Login = LoginResult.FactoryRefused; });
        Assert.Null(Classifier.NextCheck(adopted, DeviceStatus.Adopted, Settings));
        var waiting = Record(r => { r.SshOpen = true; r.Login = LoginResult.FactoryAccepted; });
        Assert.True(Classifier.NextCheck(waiting, DeviceStatus.WaitingForApproval, Settings) is not null);
    }

    [Test]
    public void FriendlyModelNames()
    {
        Assert.Equal("UniFi AC Pro", Classifier.FriendlyModel("UAP-AC-Pro-Gen2", null));
        Assert.Equal("UniFi AC Pro", Classifier.FriendlyModel(null, "U7PG2"));
        Assert.Equal("UniFi AC Lite", Classifier.FriendlyModel("UAP-AC-Lite", null));
        Assert.Equal("UniFi access point", Classifier.FriendlyModel(null, null));
    }
}

internal sealed class InventoryTests
{
    [Test]
    public void DiscoveryAndArpMergeIntoOneRecordPerMac()
    {
        var inventory = new DeviceInventory(new AssistantSettings(), null);
        var lan = FakeNetwork.Lan();
        var reply = UbntDiscoveryProtocol.TryParse(DiscoveryProtocolTests.BuildReply("80:2A:A8:12:AB:CD", "192.168.10.23"), IPAddress.Parse("192.168.10.23"))!;
        inventory.ObserveDiscovery(reply, new[] { lan }, DateTime.Now);
        inventory.ObserveNeighbor(new NeighborEntry(IPAddress.Parse("192.168.10.23"), MacAddress.Parse("802AA812ABCD")), new AssistantSettings().AllUbiquitiOuis, DateTime.Now);
        inventory.ObserveNeighbor(new NeighborEntry(IPAddress.Parse("192.168.10.40"), MacAddress.Parse("3C528299ABCD")), new AssistantSettings().AllUbiquitiOuis, DateTime.Now);

        var devices = inventory.Snapshot();
        Assert.Equal(1, devices.Count, "the non-Ubiquiti device is ignored");
        Assert.Equal("arp, discovery", string.Join(", ", devices[0].Sources));
        Assert.Equal("UniFi AC Pro", devices[0].FriendlyModel);
        Assert.Equal(DeviceStatus.Checking, devices[0].Status);
    }

    [Test]
    public void SuffixSearchMatchesTheEndOfTheMac()
    {
        var mac = MacAddress.Parse("80:2A:A8:12:AB:CD");
        Assert.True(mac.EndsWithHex("abcd"));
        Assert.True(mac.EndsWithHex("12:ab:cd"));
        Assert.True(mac.EndsWithHex("CD"));
        Assert.False(mac.EndsWithHex("C"));
        Assert.False(mac.EndsWithHex("ABCE"));
    }

    [Test]
    public void HandOffMessageListsEveryAccessPoint()
    {
        var views = new[]
        {
            View("802AA812ABCD", "192.168.10.23"),
            View("802AA812ABCE", "192.168.10.24"),
        };
        var text = HandOff.Compose(views, "the ITO Networks Team");
        Assert.Contains("Hello the ITO Networks Team", text);
        Assert.Contains("Please approve these 2 access points", text);
        Assert.Contains("80:2A:A8:12:AB:CD", text);
        Assert.Contains("192.168.10.24", text);
    }

    internal static DeviceView View(string mac, string ip) =>
        new(MacAddress.Parse(mac), IPAddress.Parse(ip), "UniFi AC Pro", "UAP-AC-Pro-Gen2", null, null, DeviceStatus.WaitingForApproval, "", JobState.None,
            null, null, DateTime.Now, DateTime.Now, DateTime.Now, null, true, new List<string>());
}

/// <summary>The live list and batches, end to end with simulated access points.</summary>
internal sealed class LiveAndBatchTests
{
    private static async Task<bool> WaitFor(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private static (AssistantHost Host, FakeAccessPoint[] Devices) Start(params FakeAccessPoint[] devices)
    {
        var settings = new AssistantSettings
        {
            VerifySeconds = 0, BatchVerifySeconds = 0, RecheckWaitingSeconds = 0, DiscoveryIntervalSeconds = 1,
            ArpSweepIntervalSeconds = 1, BatchConcurrency = 2, CheckConcurrency = 4,
        };
        var replies = devices.Select(d => UbntDiscoveryProtocol.TryParse(DiscoveryProtocolTests.BuildReply(d.Mac.ToString(), d.Address.ToString()), d.Address)!).ToList();
        var deps = new WorkflowDependencies(
            new FakeNetwork(FakeNetwork.Lan()),
            new FakeDiscovery(replies),
            new FakeNeighbors(devices.Select(d => new NeighborEntry(d.Address, d.Mac)).ToArray()),
            new FakeControllerProbe(),
            new FakeBannerProbe(),
            new FakeSshConnector(devices),
            new JsonHostKeyStore(Path.Combine(Path.GetTempPath(), $"hk-{Guid.NewGuid():N}.json")),
            null);
        var host = new AssistantHost(settings, deps, new DiagnosticLog(null));
        host.Monitor.Tick = TimeSpan.FromMilliseconds(40);
        host.Batches.RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
        return (host, devices);
    }

    private static FakeAccessPoint Ap(string mac, string ip, string password = "ubnt") =>
        new() { Mac = MacAddress.Parse(mac), Address = IPAddress.Parse(ip), Password = password };

    private static DeviceStatus StatusOf(AssistantHost host, FakeAccessPoint ap) => host.Inventory.Get(ap.Mac)?.Status ?? DeviceStatus.Checking;

    [Test]
    public async Task LiveListSortsDevicesAndLeavesAdoptedOnesAlone()
    {
        var (host, d) = Start(Ap("802AA8000001", "192.168.10.21"), Ap("802AA8000002", "192.168.10.22"), Ap("802AA8000003", "192.168.10.23", "site-secret"));
        using (host)
        {
            host.Monitor.Start();
            Assert.True(await WaitFor(() => StatusOf(host, d[0]) == DeviceStatus.Ready && StatusOf(host, d[1]) == DeviceStatus.Ready && StatusOf(host, d[2]) == DeviceStatus.Adopted),
                "two ready, one adopted");
            await Task.Delay(400); // many monitor ticks
            Assert.Equal(1, d[2].LoginAttempts.Count, "an adopted access point is tried once, then left alone");
            Assert.False(d.Any(x => x.WasChanged), "checks never change anything");
        }
    }

    [Test]
    public async Task BatchSendsAllThenDetectsAdoptionWhenTheTeamApproves()
    {
        var (host, d) = Start(Ap("802AA8000001", "192.168.10.21"), Ap("802AA8000002", "192.168.10.22"));
        using (host)
        {
            host.Monitor.Start();
            Assert.True(await WaitFor(() => d.All(x => StatusOf(host, x) == DeviceStatus.Ready)));

            var result = await host.Batches.RunAsync(d.Select(x => x.Mac).ToList(), false, null, CancellationToken.None);
            Assert.True(result.Items.All(i => i.Outcome == AdoptionOutcome.PendingAdoption), "both sent");
            Assert.True(d.All(x => x.InformUrl == "http://unifi.oneacrefund.org:8080/inform"));
            Assert.True(d.All(x => StatusOf(host, x) == DeviceStatus.WaitingForApproval));

            // The ITO Networks Team approves: the controller replaces the factory password.
            foreach (var ap in d)
            {
                ap.Password = "site-secret";
            }

            Assert.True(await WaitFor(() => d.All(x => StatusOf(host, x) == DeviceStatus.Adopted)), "adoption detected by the re-check");
            Assert.True(d.All(x => host.Inventory.Get(x.Mac)!.AdoptedDetectedAt is not null));
        }
    }

    [Test]
    public async Task BatchRetriesTemporaryFailuresAndRespectsTheConcurrencyLimit()
    {
        var devices = Enumerable.Range(1, 5).Select(i => Ap($"802AA80000{i:D2}", $"192.168.10.{20 + i}")).ToArray();
        foreach (var ap in devices)
        {
            ap.CommandDelay = TimeSpan.FromMilliseconds(30);
        }

        var (host, d) = Start(devices);
        using (host)
        {
            host.Monitor.Start();
            Assert.True(await WaitFor(() => d.All(x => StatusOf(host, x) == DeviceStatus.Ready)));
            host.Monitor.Dispose(); // stop background checks so only the batch runs commands
            d[0].FailConnectionsRemaining = 1; // the first connection attempt in the batch drops

            FakeSshConnector.FakeSession.MaxConcurrent = 0;
            var result = await host.Batches.RunAsync(d.Select(x => x.Mac).ToList(), false, null, CancellationToken.None);
            Assert.True(result.Items.All(i => i.Outcome == AdoptionOutcome.PendingAdoption));
            Assert.Equal(2, result.Items.Single(i => i.Mac == d[0].Mac).Attempts, "retried once");
            Assert.True(FakeSshConnector.FakeSession.MaxConcurrent <= 2, $"at most 2 at once (saw {FakeSshConnector.FakeSession.MaxConcurrent})");
        }
    }

    [Test]
    public async Task BatchSkipsAdoptedDevicesAndStopsCleanly()
    {
        var (host, d) = Start(Ap("802AA8000001", "192.168.10.21"), Ap("802AA8000002", "192.168.10.22", "site-secret"));
        using (host)
        {
            host.Monitor.Start();
            Assert.True(await WaitFor(() => StatusOf(host, d[1]) == DeviceStatus.Adopted && StatusOf(host, d[0]) == DeviceStatus.Ready));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var stopped = await host.Batches.RunAsync(new[] { d[0].Mac }, false, null, cancelled.Token);
            Assert.Equal(AdoptionOutcome.Cancelled, stopped.Items.Single().Outcome);
            Assert.False(d[0].WasChanged, "stopping before the start changes nothing");

            var result = await host.Batches.RunAsync(new[] { d[1].Mac }, false, null, CancellationToken.None);
            Assert.Equal(AdoptionOutcome.AlreadyAdopted, result.Items.Single().Outcome);
            Assert.False(d[1].WasChanged);
        }
    }

    [Test]
    public async Task TestModeBatchChangesNothing()
    {
        var (host, d) = Start(Ap("802AA8000001", "192.168.10.21"));
        using (host)
        {
            host.Monitor.Start();
            Assert.True(await WaitFor(() => StatusOf(host, d[0]) == DeviceStatus.Ready));
            var result = await host.Batches.RunAsync(new[] { d[0].Mac }, true, null, CancellationToken.None);
            Assert.Equal(AdoptionOutcome.DryRunCompleted, result.Items.Single().Outcome);
            Assert.False(d[0].WasChanged);
        }
    }
}

internal sealed class MiniJsonTests
{
    [Test]
    public void ParsesHandEditedJson()
    {
        var json = (Dictionary<string, object?>)MiniJson.Parse("""
            {
              // comment
              "a": "text \u00e9 \"quoted\"", /* block */
              "n": -12.5e1,
              "list": [1, true, null, "x",],
              "obj": { "k": "v" },
            }
            """)!;
        Assert.Equal("text \u00e9 \"quoted\"", (string)json["A"]!, "keys are case-insensitive");
        Assert.Equal(-125.0, (double)json["n"]!);
        Assert.Equal(4, ((List<object?>)json["list"]!).Count);
        Assert.Equal("v", (string)((Dictionary<string, object?>)json["obj"]!)["k"]!);
    }

    [Test]
    public void RoundTripsAndRejectsBrokenInput()
    {
        var original = new Dictionary<string, object?> { ["s"] = "line\nbreak \"q\"", ["b"] = false, ["i"] = 3, ["l"] = new List<object?> { "a", 1.5 } };
        var parsed = (Dictionary<string, object?>)MiniJson.Parse(MiniJson.Serialize(original))!;
        Assert.Equal("line\nbreak \"q\"", (string)parsed["s"]!);
        Assert.Equal(false, (bool)parsed["b"]!);
        Assert.Equal(3.0, (double)parsed["i"]!);

        foreach (var bad in new[] { "{", "{\"a\" 1}", "[1 2]", "\"open", "{} x", "nul" })
        {
            var threw = false;
            try
            {
                MiniJson.Parse(bad);
            }
            catch (FormatException)
            {
                threw = true;
            }

            Assert.True(threw, $"should reject: {bad}");
        }
    }
}
