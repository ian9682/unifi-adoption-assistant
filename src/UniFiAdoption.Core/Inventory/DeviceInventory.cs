using System.Net;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Device;
using UniFiAdoption.Core.Discovery;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.Core.Inventory;

/// <summary>What the technician sees for each access point.</summary>
public enum DeviceStatus
{
    Checking,
    Ready,
    Working,
    WaitingForApproval,
    Adopted,
    NeedsAttention,
    NotResponding,
}

public enum JobState
{
    None,
    Queued,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>An immutable snapshot of one access point, for display.</summary>
public sealed record DeviceView(
    MacAddress Mac,
    IPAddress? Address,
    string FriendlyModel,
    string? Model,
    string? Firmware,
    string? Hostname,
    DeviceStatus Status,
    string Reason,
    JobState Job,
    string? JobStep,
    AdoptionReport? LastReport,
    DateTime FirstSeen,
    DateTime LastSeen,
    DateTime? LastChecked,
    DateTime? AdoptedDetectedAt,
    bool ConfirmedUbiquiti,
    IReadOnlyList<string> Sources);

/// <summary>Where the search stands, for the status strip.</summary>
public sealed record NetworkSnapshot(
    bool HasNetwork,
    bool SelfAssignedOnly,
    string? AdapterName,
    string? SubnetText,
    bool Searching,
    DateTime? LastSearchCompleted,
    ControllerReachability? Controller,
    bool SearchBlocked);

/// <summary>Everything known about one MAC address. Owned and locked by <see cref="DeviceInventory"/>.</summary>
internal sealed class DeviceRecord(MacAddress mac, DateTime now)
{
    public MacAddress Mac { get; } = mac;

    public IPAddress? Address { get; set; }

    public string? Model { get; set; }

    public string? DiscoveryModel { get; set; }

    public string? Firmware { get; set; }

    public string? Hostname { get; set; }

    public bool ConfirmedUbiquiti { get; set; }

    public HashSet<string> Sources { get; } = new();

    public DateTime FirstSeen { get; } = now;

    public DateTime LastSeen { get; set; } = now;

    public DateTime? LastChecked { get; set; }

    public bool CheckInProgress { get; set; }

    public bool? SshOpen { get; set; }

    public LoginResult Login { get; set; } = LoginResult.NotTried;

    public string? CheckProblem { get; set; }

    public DeviceInfo? Info { get; set; }

    public DateTime? InformSentAt { get; set; }

    public DateTime? AdoptedDetectedAt { get; set; }

    public JobState Job { get; set; }

    public string? JobStep { get; set; }

    public DateTime? JobFinishedAt { get; set; }

    public AdoptionReport? LastReport { get; set; }
}

/// <summary>
/// Turns the facts about an access point into one plain-language status. The rule agreed with the
/// ITO Networks Team: SSH open and the factory password refused means the controller has adopted it.
/// </summary>
public static class Classifier
{
    internal static (DeviceStatus Status, string Reason) Classify(DeviceRecord r, DateTime now, AssistantSettings settings, ControllerTarget? controller)
    {
        var support = settings.SupportContact;
        if (r.Job is JobState.Queued)
        {
            return (DeviceStatus.Working, r.JobStep ?? "Waiting to start…");
        }

        if (r.Job is JobState.Running)
        {
            return (DeviceStatus.Working, r.JobStep ?? "Setting up…");
        }

        if (r.Job is JobState.Failed && (r.LastChecked is null || r.LastChecked < r.JobFinishedAt))
        {
            return (DeviceStatus.NeedsAttention, r.LastReport?.Title ?? "Setup didn't finish.");
        }

        var recentlySent = r.InformSentAt is { } sent && now - sent < TimeSpan.FromMinutes(30) && r.Login != LoginResult.FactoryRefused;
        var stale = now - r.LastSeen > TimeSpan.FromSeconds(settings.UnreachableAfterSeconds);
        if (stale)
        {
            return recentlySent
                ? (DeviceStatus.WaitingForApproval, "Restarting. This is normal while it is being approved.")
                : (DeviceStatus.NotResponding, "Not responding. Check its power and cable.");
        }

        if (r.Login == LoginResult.FactoryRefused && r.SshOpen == true)
        {
            return (DeviceStatus.Adopted, "Adopted. It no longer accepts the factory password.");
        }

        if (r.SshOpen == false)
        {
            return recentlySent
                ? (DeviceStatus.WaitingForApproval, "Restarting. This is normal while it is being approved.")
                : (DeviceStatus.NeedsAttention, "Not accepting setup connections yet. If it was just plugged in, wait 2 minutes.");
        }

        switch (r.Login)
        {
            case LoginResult.NotTried when r.SshOpen is null || r.LastChecked is null:
                return (DeviceStatus.Checking, "Checking…");
            case LoginResult.NotTried:
                return (DeviceStatus.NeedsAttention, "Can't be checked because the factory login is turned off in settings.");
            case LoginResult.HostKeyRejected:
                return (DeviceStatus.NeedsAttention, $"Security check failed. Contact {support}.");
            case LoginResult.IdentityMismatch:
                return (DeviceStatus.NeedsAttention, "Its details didn't match when checked. Refresh to try again.");
            case LoginResult.Failed:
                return recentlySent
                    ? (DeviceStatus.WaitingForApproval, "Restarting. This is normal while it is being approved.")
                    : (DeviceStatus.NeedsAttention, "Couldn't be checked. Refresh to try again.");
        }

        // Factory password accepted: the access point told us its own status.
        if (r.Info?.Model is { } model && !settings.SupportedModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return (DeviceStatus.NeedsAttention, $"This is a {model}, which this tool doesn't set up.");
        }

        if (controller is not null && controller.Matches(r.Info?.StatusUrl ?? (recentlySent ? controller.InformUrl : null)))
        {
            return r.Info?.State == DeviceInformState.Connected
                ? (DeviceStatus.Adopted, "Adopted and connected to the central controller.")
                : (DeviceStatus.WaitingForApproval, $"Sent to the central controller. Waiting for {support} to approve it.");
        }

        return (DeviceStatus.Ready, "Ready to set up.");
    }

    /// <summary>When the live list should check this access point again (null: not automatically).</summary>
    internal static DateTime? NextCheck(DeviceRecord r, DeviceStatus status, AssistantSettings settings)
    {
        if (r.CheckInProgress || r.Job is JobState.Queued or JobState.Running)
        {
            return null;
        }

        if (r.LastChecked is not { } last)
        {
            return DateTime.MinValue;
        }

        return status switch
        {
            DeviceStatus.Checking => DateTime.MinValue,
            DeviceStatus.WaitingForApproval => last.AddSeconds(settings.RecheckWaitingSeconds),
            DeviceStatus.Ready => last.AddMinutes(5),
            DeviceStatus.NeedsAttention => last.AddMinutes(2),
            DeviceStatus.NotResponding => r.LastSeen > last ? DateTime.MinValue : null,
            _ => null, // adopted access points are left alone
        };
    }

    /// <summary>"UniFi AC Pro" rather than "UAP-AC-Pro-Gen2" or "U7PG2".</summary>
    public static string FriendlyModel(string? model, string? discoveryModel)
    {
        var text = (model ?? discoveryModel ?? string.Empty).Trim();
        if (text.StartsWith("UAP-AC-Pro", StringComparison.OrdinalIgnoreCase) || text.Equals("U7PG2", StringComparison.OrdinalIgnoreCase))
        {
            return "UniFi AC Pro";
        }

        if (text.StartsWith("UAP-", StringComparison.OrdinalIgnoreCase))
        {
            return "UniFi " + text.Substring(4).Replace('-', ' ');
        }

        return text.Length > 0 ? $"UniFi access point ({text})" : "UniFi access point";
    }
}

/// <summary>
/// The single list of access points. Every source (discovery broadcast, ARP sweep, read-only checks,
/// batches) updates the same record per MAC address. Thread-safe; the window polls <see cref="Version"/>.
/// </summary>
public sealed class DeviceInventory(AssistantSettings settings, ControllerTarget? controller)
{
    private readonly object _gate = new();
    private readonly Dictionary<MacAddress, DeviceRecord> _devices = new();
    private int _version;
    private NetworkSnapshot _network = new(true, false, null, null, true, null, null, false);

    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public NetworkSnapshot Network
    {
        get
        {
            lock (_gate)
            {
                return _network;
            }
        }
    }

    public void SetNetwork(Func<NetworkSnapshot, NetworkSnapshot> change)
    {
        lock (_gate)
        {
            _network = change(_network);
            _version++;
        }
    }

    public void ObserveDiscovery(UbntDiscoveryReply reply, IReadOnlyList<LocalInterfaceInfo> interfaces, DateTime now)
    {
        var address = new[] { reply.Source }.Concat(reply.ReportedAddresses)
            .FirstOrDefault(a => interfaces.Any(i => i.Subnet.Contains(a)));
        Touch(reply.Mac, now, "discovery", r =>
        {
            r.ConfirmedUbiquiti = true;
            r.Address = address ?? r.Address;
            r.DiscoveryModel = reply.ModelDisplay ?? reply.Model ?? r.DiscoveryModel;
            r.Firmware ??= reply.Firmware;
            r.Hostname = reply.Hostname ?? r.Hostname;
        });
    }

    public void ObserveNeighbor(NeighborEntry entry, IReadOnlyCollection<string> ubiquitiOuis, DateTime now)
    {
        lock (_gate)
        {
            // Only Ubiquiti hardware belongs in the list; everything else on the network is ignored.
            if (!_devices.ContainsKey(entry.Mac) && !ubiquitiOuis.Contains(entry.Mac.Oui, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
        }

        Touch(entry.Mac, now, "arp", r => r.Address = entry.Address);
    }

    internal void BeginCheck(MacAddress mac) => Update(mac, r => r.CheckInProgress = true);

    internal void ApplyCheck(MacAddress mac, DeviceCheck check, DateTime now) => Update(mac, r =>
    {
        r.CheckInProgress = false;
        r.LastChecked = now;
        r.SshOpen = check.SshOpen;
        r.CheckProblem = check.Problem;
        if (check.SshOpen)
        {
            r.LastSeen = now;
        }

        if (check.Login != LoginResult.NotTried || !check.SshOpen)
        {
            r.Login = check.Login;
        }

        if (check.Info is { } info)
        {
            r.Info = info;
            r.Model = info.Model ?? r.Model;
            r.Firmware = info.Version ?? r.Firmware;
            if (info.Mac is { } && r.Address is null && IPAddress.TryParse(info.IpAddress ?? string.Empty, out var ip))
            {
                r.Address = ip;
            }
        }

        if (check.Login == LoginResult.FactoryRefused && check.SshOpen && r.AdoptedDetectedAt is null)
        {
            r.AdoptedDetectedAt = now;
        }
    });

    internal void SetJob(MacAddress mac, JobState state, string? step) => Update(mac, r =>
    {
        r.Job = state;
        r.JobStep = step;
    });

    internal void CompleteJob(MacAddress mac, JobState state, AdoptionReport report, RunState run, DateTime now) => Update(mac, r =>
    {
        r.Job = state;
        r.JobStep = null;
        r.JobFinishedAt = now;
        r.LastReport = report;
        r.Model = run.Model ?? r.Model;
        r.Firmware = run.Firmware ?? r.Firmware;
        switch (report.Outcome)
        {
            case AdoptionOutcome.AlreadyAdopted:
                r.SshOpen = true;
                r.Login = LoginResult.FactoryRefused;
                r.LastChecked = now;
                r.AdoptedDetectedAt ??= now;
                break;
            case AdoptionOutcome.Connected:
                r.SshOpen = true;
                r.Login = LoginResult.FactoryAccepted;
                r.LastChecked = now;
                r.Info = new DeviceInfo(run.Model, run.Firmware, mac, r.Address?.ToString(), r.Hostname, "Connected",
                    run.StatusUrl ?? controller?.InformUrl, DeviceInformState.Connected, new Dictionary<string, string>());
                r.AdoptedDetectedAt ??= now;
                break;
            case AdoptionOutcome.PendingAdoption or AdoptionOutcome.InformSentNotVerified or
                AdoptionOutcome.ControllerUnreachable or AdoptionOutcome.ControllerNameUnresolvable or AdoptionOutcome.ControllerRejected:
                r.SshOpen = true;
                r.Login = LoginResult.FactoryAccepted;
                r.LastChecked = now;
                r.InformSentAt = now;
                r.Info = new DeviceInfo(run.Model, run.Firmware, mac, r.Address?.ToString(), r.Hostname, run.StatusText,
                    controller?.InformUrl, DeviceInfoParser.Classify(run.StatusText), new Dictionary<string, string>());
                break;
        }
    });

    /// <summary>Forget check results so the next pass re-checks everything except adopted access points.</summary>
    public void RequestRecheck()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            foreach (var r in _devices.Values)
            {
                if (Classifier.Classify(r, now, settings, controller).Status != DeviceStatus.Adopted && !r.CheckInProgress)
                {
                    r.LastChecked = null;
                }
            }

            _version++;
        }
    }

    public DeviceView? Get(MacAddress mac)
    {
        lock (_gate)
        {
            return _devices.TryGetValue(mac, out var r) ? View(r, DateTime.Now) : null;
        }
    }

    public IReadOnlyList<DeviceView> Snapshot()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            return _devices.Values.Select(r => View(r, now)).OrderBy(v => v.Mac.Hex, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Access points the live list should check now, oldest first.</summary>
    internal IReadOnlyList<(MacAddress Mac, IPAddress Address)> DueForCheck(DateTime now)
    {
        lock (_gate)
        {
            return _devices.Values
                .Where(r => r.Address is not null)
                .Select(r => (Record: r, Due: Classifier.NextCheck(r, Classifier.Classify(r, now, settings, controller).Status, settings)))
                .Where(x => x.Due is { } due && due <= now)
                .OrderBy(x => x.Due)
                .Select(x => (x.Record.Mac, x.Record.Address!))
                .ToList();
        }
    }

    private void Touch(MacAddress mac, DateTime now, string source, Action<DeviceRecord> change)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(mac, out var record))
            {
                record = new DeviceRecord(mac, now);
                _devices[mac] = record;
            }

            record.LastSeen = now;
            record.Sources.Add(source);
            change(record);
            _version++;
        }
    }

    private void Update(MacAddress mac, Action<DeviceRecord> change)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(mac, out var record))
            {
                change(record);
                _version++;
            }
        }
    }

    private DeviceView View(DeviceRecord r, DateTime now)
    {
        var (status, reason) = Classifier.Classify(r, now, settings, controller);
        return new DeviceView(
            r.Mac, r.Address, Classifier.FriendlyModel(r.Model, r.DiscoveryModel), r.Model ?? r.DiscoveryModel, r.Firmware, r.Hostname,
            status, reason, r.Job, r.JobStep, r.LastReport, r.FirstSeen, r.LastSeen, r.LastChecked, r.AdoptedDetectedAt,
            r.ConfirmedUbiquiti, r.Sources.OrderBy(s => s, StringComparer.Ordinal).ToList());
    }
}
