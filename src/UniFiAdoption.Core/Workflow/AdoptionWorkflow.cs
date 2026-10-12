using System.Net;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Device;
using UniFiAdoption.Core.Discovery;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Security;
using UniFiAdoption.Core.Ssh;

namespace UniFiAdoption.Core.Workflow;

public sealed record AdoptionRequest(string MacSuffixText, bool TestMode, SshCredential? SessionCredential);

public sealed record WorkflowDependencies(
    ILocalNetworkProvider Network,
    IUbntDiscovery UbntDiscovery,
    INeighborScanner NeighborScanner,
    IControllerProbe ControllerProbe,
    ISshBannerProbe BannerProbe,
    ISshConnector Ssh,
    IHostKeyStore HostKeys,
    ICredentialStore? SavedCredentials);

/// <summary>
/// One run of "find and configure": validate input → find exactly one matching device → confirm it
/// is a UniFi device → log in → confirm its identity from the inside → point it at the controller →
/// read back its status. Anything uncertain stops the run before the device is changed.
/// </summary>
public sealed class AdoptionWorkflow(AssistantSettings settings, WorkflowDependencies deps, DiagnosticLog log)
{
    public async Task<AdoptionReport> RunAsync(AdoptionRequest request, IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {
        var run = new RunState();
        log.Info($"--- Run started. Assistant {AdoptionReport.AssistantVersion}. Test mode: {request.TestMode}. Inform URL: {settings.InformUrl}");
        try
        {
            var (outcome, detail) = await RunCoreAsync(request, run, progress, cancellationToken).ConfigureAwait(false);
            return Finish(outcome, run, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Warn("Run cancelled by the user.");
            return Finish(AdoptionOutcome.Cancelled, run, null);
        }
        catch (Exception ex)
        {
            log.Error("Unexpected error.", ex);
            run.Notes.Add($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            return Finish(AdoptionOutcome.UnexpectedError, run, null);
        }
    }

    private async Task<(AdoptionOutcome Outcome, string? Detail)> RunCoreAsync(
        AdoptionRequest request, RunState run, IProgress<WorkflowProgress>? progress, CancellationToken ct)
    {
        void Report(int percent, string message) => progress?.Report(new WorkflowProgress(percent, message));

        // 1. Input and settings ---------------------------------------------------------------
        Report(2, "Checking what you typed…");
        var validation = MacSuffix.Validate(request.MacSuffixText);
        if (!validation.IsValid)
        {
            log.Info($"Input rejected: {validation.Problem}");
            return (AdoptionOutcome.InvalidInput, validation.Problem);
        }

        var suffix = validation.Suffix!;
        run.Suffix = suffix.Value;
        log.Info($"Looking for a device whose MAC ends in {suffix.Value}.");

        var settingsProblems = settings.Validate();
        if (settingsProblems.Count > 0 || !ControllerTarget.TryCreate(settings.InformUrl, out var controller, out _))
        {
            foreach (var problem in settingsProblems)
            {
                log.Error($"Settings problem: {problem}");
                run.Notes.Add($"Settings problem: {problem}");
            }

            return (AdoptionOutcome.ConfigurationError, null);
        }

        // 2. This computer's network -------------------------------------------------------------
        Report(5, "Checking this computer's network connection…");
        var allInterfaces = deps.Network.GetIPv4Interfaces();
        foreach (var nic in allInterfaces)
        {
            log.Info($"Adapter: {nic}");
        }

        var usable = allInterfaces.Where(i => !i.IsSelfAssigned).ToList();
        if (usable.Count == 0)
        {
            run.OnlySelfAssignedAddress = allInterfaces.Count > 0;
            log.Warn("No usable IPv4 adapter.");
            return (AdoptionOutcome.NoNetwork, null);
        }

        var scanInterfaces = usable.Where(i => !i.LooksVirtual).ToList();
        if (scanInterfaces.Count == 0)
        {
            scanInterfaces = usable;
        }

        // 3. Discovery (Ubiquiti broadcast + ARP sweep), controller check in parallel --------------
        Report(8, "Looking for the access point on this network…");
        var controllerTask = deps.ControllerProbe.ProbeAsync(controller, TimeSpan.FromSeconds(6), ct);
        var discoveryTask = deps.UbntDiscovery.DiscoverAsync(scanInterfaces, TimeSpan.FromSeconds(settings.DiscoveryListenSeconds), ct);
        var arpProgress = new InlineProgress<double>(f => Report(8 + (int)(f * 47), "Looking for the access point on this network…"));
        var arpTask = deps.NeighborScanner.ScanAsync(scanInterfaces, settings.MaxHostsPerInterface, settings.ArpParallelism, arpProgress, ct);

        await Task.WhenAll(discoveryTask, arpTask).ConfigureAwait(false);
        var discovery = await discoveryTask.ConfigureAwait(false);
        var arp = await arpTask.ConfigureAwait(false);
        log.Info($"Ubiquiti discovery replies: {discovery.Replies.Count}. ARP: {arp.Entries.Count} answers from {arp.AddressesProbed} addresses probed.");
        foreach (var reply in discovery.Replies)
        {
            log.Info($"Discovery reply: {reply}");
        }

        run.FromThisComputer = await controllerTask.ConfigureAwait(false);
        log.Info($"Controller check from this computer: {run.FromThisComputer}");
        if (!run.FromThisComputer.PortReachable)
        {
            run.Notes.Add($"This computer could not reach the controller: {run.FromThisComputer.Problem}");
        }

        var located = CandidateResolver.Resolve(
            suffix, discovery.Replies, arp.Entries, scanInterfaces, settings.AllUbiquitiOuis, settings.RequireUbiquitiIdentity);
        run.DevicesSeen = located.DevicesSeen;
        run.MatchCount = located.Matches.Count;
        foreach (var match in located.Matches)
        {
            log.Info($"Matching device: {match}");
        }

        if (located.Device is { } locatedDevice)
        {
            run.Mac = locatedDevice.Mac.ToString();
            run.IpAddress = located.Address?.ToString();
            run.Model = locatedDevice.Discovery?.ModelDisplay ?? locatedDevice.Discovery?.Model;
            run.Firmware = locatedDevice.Discovery?.Firmware;
            run.DiscoverySaysFactoryDefault = locatedDevice.Discovery?.IsFactoryDefault;
        }

        switch (located.Status)
        {
            case LocateStatus.NotFound:
                log.Warn($"No device ending in {suffix.Value}. Devices seen: {located.DevicesSeen}.");
                return (discovery.AccessDenied || arp.AccessDenied) && located.DevicesSeen == 0
                    ? (AdoptionOutcome.PermissionDenied, (string?)null)
                    : (AdoptionOutcome.NotFound, (string?)null);
            case LocateStatus.MultipleMatches:
                log.Warn($"{located.Matches.Count} devices end in {suffix.Value}; refusing to choose one.");
                run.Notes.AddRange(located.Matches.Select(m => $"Matching device: {m}"));
                return (AdoptionOutcome.MultipleMatches, null);
            case LocateStatus.AmbiguousAddress:
                log.Warn("The matching device has several addresses on this network; refusing to choose one.");
                run.Notes.Add($"Matching device: {located.Device}");
                return (AdoptionOutcome.AmbiguousAddress, null);
            case LocateStatus.NotUnifiDevice:
                log.Warn($"{located.Device!.Mac} did not answer Ubiquiti discovery and its OUI {located.Device.Mac.Oui} is not a known Ubiquiti OUI.");
                run.Notes.Add($"OUI {located.Device.Mac.Oui} is not in the Ubiquiti list. If it is a Ubiquiti device, add it to ExtraUbiquitiOuis.");
                return (AdoptionOutcome.NotUnifiDevice, null);
            case LocateStatus.OnOtherNetwork:
                log.Warn($"{located.Device!.Mac} answered discovery from {located.Address}, outside this computer's networks.");
                return (AdoptionOutcome.OnOtherNetwork, null);
        }

        var device = located.Device!;
        var address = located.Address!;
        log.Info($"Selected {device.Mac} at {address}.");

        var adopter = new DeviceAdopter(settings, deps, log);
        return await adopter.AdoptAsync(run, device.Mac, address, request.TestMode, request.SessionCredential, settings.VerifySeconds, Report, ct)
            .ConfigureAwait(false);
    }

    private AdoptionReport Finish(AdoptionOutcome outcome, RunState run, string? detail) =>
        ReportBuilder.Build(settings, run, outcome, detail, log);

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
