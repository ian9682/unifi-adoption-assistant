using System.Globalization;
using System.Net;
using System.Text;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Ssh;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.Core.Inventory;

/// <summary>
/// Keeps the list live: repeats the discovery broadcast, sweeps ARP now and then, checks the controller
/// from this computer, and runs read-only checks on access points that are due. Low traffic by design:
/// one small broadcast every 30 s, one ARP sweep every 2 min, and SSH only to Ubiquiti devices.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private readonly AssistantSettings _settings;
    private readonly WorkflowDependencies _deps;
    private readonly DeviceInventory _inventory;
    private readonly DeviceAdopter _adopter;
    private readonly DiagnosticLog _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Task? _loop;
    private volatile bool _fullRefresh = true;

    public NetworkMonitor(AssistantSettings settings, WorkflowDependencies deps, DeviceInventory inventory, DeviceAdopter adopter, DiagnosticLog log)
    {
        _settings = settings;
        _deps = deps;
        _inventory = inventory;
        _adopter = adopter;
        _log = log;
    }

    /// <summary>How often the loop wakes up to run due checks (shortened in tests).</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(5);

    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>Search again now: broadcast, ARP sweep and re-check every access point that isn't adopted.</summary>
    public void Refresh()
    {
        _fullRefresh = true;
        _inventory.RequestRecheck();
        _wake.Release();
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Stopping.
        }

        _stop.Dispose();
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        var lastDiscovery = DateTime.MinValue;
        var lastArp = DateTime.MinValue;
        var lastController = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var full = _fullRefresh;
                _fullRefresh = false;
                if (full || now - lastDiscovery >= TimeSpan.FromSeconds(_settings.DiscoveryIntervalSeconds))
                {
                    var arp = full || now - lastArp >= TimeSpan.FromSeconds(_settings.ArpSweepIntervalSeconds);
                    var controller = full || now - lastController >= TimeSpan.FromMinutes(2);
                    await SearchAsync(arp, controller, ct).ConfigureAwait(false);
                    lastDiscovery = DateTime.Now;
                    lastArp = arp ? lastDiscovery : lastArp;
                    lastController = controller ? lastDiscovery : lastController;
                }

                await RunDueChecksAsync(ct).ConfigureAwait(false);
                await _wake.WaitAsync(Tick, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("Live search problem.", ex);
                try
                {
                    await Task.Delay(Tick, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task SearchAsync(bool sweepArp, bool checkController, CancellationToken ct)
    {
        var all = _deps.Network.GetIPv4Interfaces();
        var usable = all.Where(i => !i.IsSelfAssigned).ToList();
        var scan = usable.Where(i => !i.LooksVirtual).ToList();
        if (scan.Count == 0)
        {
            scan = usable;
        }

        if (scan.Count == 0)
        {
            _inventory.SetNetwork(n => n with { HasNetwork = false, SelfAssignedOnly = all.Count > 0, Searching = false, LastSearchCompleted = DateTime.Now });
            return;
        }

        var primary = scan[0];
        _inventory.SetNetwork(n => n with
        {
            HasNetwork = true,
            SelfAssignedOnly = false,
            AdapterName = primary.Name,
            SubnetText = primary.Subnet.ToString(),
            Searching = true,
        });

        var controllerTask = checkController && _adopter.Controller is { } target
            ? _deps.ControllerProbe.ProbeAsync(target, TimeSpan.FromSeconds(6), ct)
            : null;
        var discoveryTask = _deps.UbntDiscovery.DiscoverAsync(scan, TimeSpan.FromSeconds(_settings.DiscoveryListenSeconds), ct);
        var arpTask = sweepArp
            ? _deps.NeighborScanner.ScanAsync(scan, _settings.MaxHostsPerInterface, _settings.ArpParallelism, null, ct)
            : null;

        var discovery = await discoveryTask.ConfigureAwait(false);
        var now = DateTime.Now;
        foreach (var reply in discovery.Replies)
        {
            _inventory.ObserveDiscovery(reply, scan, now);
        }

        var blocked = discovery.AccessDenied;
        if (arpTask is not null)
        {
            var arp = await arpTask.ConfigureAwait(false);
            blocked |= arp.AccessDenied && arp.Entries.Count == 0;
            var ouis = _settings.AllUbiquitiOuis;
            foreach (var entry in arp.Entries)
            {
                _inventory.ObserveNeighbor(entry, ouis, DateTime.Now);
            }
        }

        var reachability = controllerTask is null ? null : await controllerTask.ConfigureAwait(false);
        _inventory.SetNetwork(n => n with
        {
            Searching = false,
            LastSearchCompleted = DateTime.Now,
            Controller = reachability ?? n.Controller,
            SearchBlocked = blocked && discovery.Replies.Count == 0,
        });
    }

    private async Task RunDueChecksAsync(CancellationToken ct)
    {
        var due = _inventory.DueForCheck(DateTime.Now);
        if (due.Count == 0)
        {
            return;
        }

        using var limiter = new SemaphoreSlim(_settings.CheckConcurrency);
        var tasks = due.Select(async item =>
        {
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _inventory.BeginCheck(item.Mac);
                DeviceCheck check;
                try
                {
                    check = await _adopter.CheckAsync(item.Mac, item.Address, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"Check {item.Mac} failed: {ex.GetType().Name}: {ex.Message}");
                    check = new DeviceCheck(true, LoginResult.Failed, null, ex.Message);
                }

                _inventory.ApplyCheck(item.Mac, check, DateTime.Now);
            }
            finally
            {
                limiter.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

public sealed record BatchItemResult(MacAddress Mac, AdoptionOutcome Outcome, AdoptionReport Report, int Attempts);

public sealed record BatchResult(IReadOnlyList<BatchItemResult> Items, DateTimeOffset Started, DateTimeOffset Finished);

/// <summary>
/// Sets up several access points with a concurrency limit. Each one is re-validated right before its
/// turn, temporary failures are retried, and stopping never interrupts a command half-way.
/// </summary>
public sealed class BatchRunner
{
    private static readonly AdoptionOutcome[] Transient =
    {
        AdoptionOutcome.SshUnavailable, AdoptionOutcome.InformFailed, AdoptionOutcome.UnexpectedError,
    };

    private readonly AssistantSettings _settings;
    private readonly DeviceAdopter _adopter;
    private readonly DeviceInventory _inventory;
    private readonly DiagnosticLog _log;
    private readonly HashSet<MacAddress> _active = new();

    public BatchRunner(AssistantSettings settings, DeviceAdopter adopter, DeviceInventory inventory, DiagnosticLog log)
    {
        _settings = settings;
        _adopter = adopter;
        _inventory = inventory;
        _log = log;
    }

    /// <summary>Waits before retry 1 and retry 2 (shortened in tests).</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

    public async Task<BatchResult> RunAsync(IReadOnlyList<MacAddress> macs, bool testMode, SshCredential? sessionCredential, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var wanted = new List<MacAddress>();
        lock (_active)
        {
            foreach (var mac in macs.Distinct())
            {
                if (_active.Add(mac))
                {
                    wanted.Add(mac);
                }
            }
        }

        _log.Info($"=== Batch of {wanted.Count} started. Test mode: {testMode}.");
        foreach (var mac in wanted)
        {
            _inventory.SetJob(mac, JobState.Queued, "Waiting to start…");
        }

        using var limiter = new SemaphoreSlim(_settings.BatchConcurrency);
        var tasks = wanted.Select(async mac =>
        {
            try
            {
                try
                {
                    await limiter.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Finish(mac, AdoptionOutcome.Cancelled, NewRun(mac, null), 0, JobState.Cancelled);
                }

                try
                {
                    return await RunOneAsync(mac, testMode, sessionCredential, ct).ConfigureAwait(false);
                }
                finally
                {
                    limiter.Release();
                }
            }
            finally
            {
                lock (_active)
                {
                    _active.Remove(mac);
                }
            }
        }).ToList();

        var items = await Task.WhenAll(tasks).ConfigureAwait(false);
        _log.Info($"=== Batch finished: {string.Join(", ", items.GroupBy(i => i.Outcome).Select(g => $"{g.Key} {g.Count()}"))}.");
        return new BatchResult(items, started, DateTimeOffset.Now);
    }

    private async Task<BatchItemResult> RunOneAsync(MacAddress mac, bool testMode, SshCredential? sessionCredential, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var view = _inventory.Get(mac);
            var run = NewRun(mac, view);
            if (ct.IsCancellationRequested)
            {
                return Finish(mac, AdoptionOutcome.Cancelled, run, attempt - 1, JobState.Cancelled);
            }

            // Re-validate right before acting: it must still be known, reachable and not adopted.
            if (view?.Address is not { } address)
            {
                run.Notes.Add("The access point is no longer listed with an address.");
                return Finish(mac, AdoptionOutcome.NotFound, run, attempt - 1, JobState.Skipped);
            }

            if (view.AdoptedDetectedAt is not null || view.LastReport?.Outcome is AdoptionOutcome.AlreadyAdopted or AdoptionOutcome.Connected)
            {
                return Finish(mac, AdoptionOutcome.AlreadyAdopted, run, attempt - 1, JobState.Skipped);
            }

            _inventory.SetJob(mac, JobState.Running, "Connecting…");
            AdoptionOutcome outcome;
            try
            {
                (outcome, _) = await _adopter.AdoptAsync(run, mac, address, testMode, sessionCredential, _settings.BatchVerifySeconds,
                    (_, step) => _inventory.SetJob(mac, JobState.Running, step), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Finish(mac, AdoptionOutcome.Cancelled, run, attempt, JobState.Cancelled);
            }
            catch (Exception ex)
            {
                _log.Error($"Unexpected error setting up {mac}.", ex);
                run.Notes.Add($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
                outcome = AdoptionOutcome.UnexpectedError;
            }

            if (Transient.Contains(outcome) && attempt <= RetryDelays.Count && !ct.IsCancellationRequested)
            {
                var wait = RetryDelays[attempt - 1];
                _log.Warn($"{mac}: {outcome}; retrying in {wait.TotalSeconds:0} s (attempt {attempt + 1}).");
                _inventory.SetJob(mac, JobState.Running, $"Didn't work. Trying again in {wait.TotalSeconds:0} seconds…");
                try
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Finish(mac, AdoptionOutcome.Cancelled, run, attempt, JobState.Cancelled);
                }

                continue;
            }

            var succeeded = outcome is AdoptionOutcome.PendingAdoption or AdoptionOutcome.Connected or AdoptionOutcome.InformSentNotVerified
                or AdoptionOutcome.ControllerUnreachable or AdoptionOutcome.ControllerNameUnresolvable or AdoptionOutcome.DryRunCompleted
                or AdoptionOutcome.AlreadyAdopted;
            return Finish(mac, outcome, run, attempt, succeeded ? JobState.Succeeded : JobState.Failed);
        }
    }

    private BatchItemResult Finish(MacAddress mac, AdoptionOutcome outcome, RunState run, int attempts, JobState state)
    {
        var report = ReportBuilder.Build(_settings, run, outcome, null, _log);
        _inventory.CompleteJob(mac, state, report, run, DateTime.Now);
        return new BatchItemResult(mac, outcome, report, attempts);
    }

    private static RunState NewRun(MacAddress mac, DeviceView? view) => new()
    {
        Suffix = mac.Hex.Substring(8),
        Mac = mac.ToString(),
        IpAddress = view?.Address?.ToString(),
        Model = view?.Model,
        Firmware = view?.Firmware,
    };
}

/// <summary>The message a technician sends so the ITO Networks Team can approve access points.</summary>
public static class HandOff
{
    public static string Compose(IEnumerable<DeviceView> waiting, string support, string? siteNote = null)
    {
        var list = waiting.ToList();
        var text = new StringBuilder();
        text.AppendLine($"Hello {support},");
        text.AppendLine();
        text.AppendLine(list.Count == 1
            ? "Please approve this access point in the UniFi controller:"
            : $"Please approve these {list.Count} access points in the UniFi controller:");
        text.AppendLine();
        foreach (var device in list)
        {
            text.AppendLine($"  - {device.FriendlyModel}  {device.Mac}  ({device.Address?.ToString() ?? "no address"})");
        }

        text.AppendLine();
        if (!string.IsNullOrWhiteSpace(siteNote))
        {
            text.AppendLine(siteNote);
        }

        text.AppendLine($"Sent from the UniFi Adoption Assistant on {Environment.MachineName}, {DateTime.Now.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}.");
        return text.ToString();
    }
}

/// <summary>Wires the engine together. The window creates one; tests can pass fake dependencies.</summary>
public sealed class AssistantHost : IDisposable
{
    public AssistantHost(AssistantSettings settings, WorkflowDependencies deps, DiagnosticLog log)
    {
        Settings = settings;
        Log = log;
        Adopter = new DeviceAdopter(settings, deps, log);
        Inventory = new DeviceInventory(settings, Adopter.Controller);
        Monitor = new NetworkMonitor(settings, deps, Inventory, Adopter, log);
        Batches = new BatchRunner(settings, Adopter, Inventory, log);
    }

    public AssistantSettings Settings { get; }

    public DiagnosticLog Log { get; }

    public DeviceAdopter Adopter { get; }

    public DeviceInventory Inventory { get; }

    public NetworkMonitor Monitor { get; }

    public BatchRunner Batches { get; }

    public void Dispose() => Monitor.Dispose();
}
