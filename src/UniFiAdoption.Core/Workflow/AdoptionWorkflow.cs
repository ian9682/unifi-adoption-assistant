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
    private const int MaxLoginAttempts = 3;

    public async Task<AdoptionReport> RunAsync(AdoptionRequest request, IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {
        var run = new RunState();
        ISshSession? session = null;
        log.Info($"--- Run started. Assistant {AdoptionReport.AssistantVersion}. Test mode: {request.TestMode}. Inform URL: {settings.InformUrl}");
        try
        {
            var (outcome, detail, openedSession) = await RunCoreAsync(request, run, progress, cancellationToken).ConfigureAwait(false);
            session = openedSession;
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
        finally
        {
            session ??= _openSession;
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _openSession = null;
        }
    }

    // Kept so the finally block can close a session if an exception escapes mid-run.
    private ISshSession? _openSession;

    private async Task<(AdoptionOutcome Outcome, string? Detail, ISshSession? Session)> RunCoreAsync(
        AdoptionRequest request, RunState run, IProgress<WorkflowProgress>? progress, CancellationToken ct)
    {
        void Report(int percent, string message) => progress?.Report(new WorkflowProgress(percent, message));

        // 1. Input and settings ---------------------------------------------------------------
        Report(2, "Checking what you typed…");
        var validation = MacSuffix.Validate(request.MacSuffixText);
        if (!validation.IsValid)
        {
            log.Info($"Input rejected: {validation.Problem}");
            return (AdoptionOutcome.InvalidInput, validation.Problem, null);
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

            return (AdoptionOutcome.ConfigurationError, null, null);
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
            return (AdoptionOutcome.NoNetwork, null, null);
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
                    ? (AdoptionOutcome.PermissionDenied, null, null)
                    : (AdoptionOutcome.NotFound, null, null);
            case LocateStatus.MultipleMatches:
                log.Warn($"{located.Matches.Count} devices end in {suffix.Value}; refusing to choose one.");
                run.Notes.AddRange(located.Matches.Select(m => $"Matching device: {m}"));
                return (AdoptionOutcome.MultipleMatches, null, null);
            case LocateStatus.AmbiguousAddress:
                log.Warn("The matching device has several addresses on this network; refusing to choose one.");
                run.Notes.Add($"Matching device: {located.Device}");
                return (AdoptionOutcome.AmbiguousAddress, null, null);
            case LocateStatus.NotUnifiDevice:
                log.Warn($"{located.Device!.Mac} did not answer Ubiquiti discovery and its OUI {located.Device.Mac.Oui} is not a known Ubiquiti OUI.");
                run.Notes.Add($"OUI {located.Device.Mac.Oui} is not in the Ubiquiti list. If it is a Ubiquiti device, add it to ExtraUbiquitiOuis.");
                return (AdoptionOutcome.NotUnifiDevice, null, null);
            case LocateStatus.OnOtherNetwork:
                log.Warn($"{located.Device!.Mac} answered discovery from {located.Address}, outside this computer's networks.");
                return (AdoptionOutcome.OnOtherNetwork, null, null);
        }

        var device = located.Device!;
        var address = located.Address!;
        log.Info($"Selected {device.Mac} at {address}.");

        // 4. SSH available? (no credentials sent) ------------------------------------------------
        Report(58, "Found it. Connecting to the access point…");
        var banner = await deps.BannerProbe.ProbeAsync(address, settings.SshPort, TimeSpan.FromSeconds(settings.SshConnectTimeoutSeconds), ct)
            .ConfigureAwait(false);
        if (!banner.Reachable)
        {
            log.Warn($"SSH not available on {address}:{settings.SshPort}: {banner.Problem}");
            run.Notes.Add($"SSH check: {banner.Problem}");
            return (AdoptionOutcome.SshUnavailable, null, null);
        }

        log.Info($"SSH server identifies as: {banner.Banner}");

        // 5. Log in -----------------------------------------------------------------------------
        var credentials = BuildCredentialList(request);
        if (credentials.Count == 0)
        {
            run.NoLoginAvailable = true;
            log.Warn("No login available: factory login disabled and none provided by IT.");
            return (AdoptionOutcome.AuthenticationFailed, null, null);
        }

        ISshSession? session = null;
        foreach (var credential in credentials)
        {
            ct.ThrowIfCancellationRequested();
            log.Info($"Trying {credential}.");
            try
            {
                session = await deps.Ssh.ConnectAsync(
                    new SshConnectRequest(
                        address,
                        settings.SshPort,
                        credential,
                        TimeSpan.FromSeconds(settings.SshConnectTimeoutSeconds),
                        (algorithm, fingerprint) =>
                        {
                            var check = deps.HostKeys.Check(device.Mac, algorithm, fingerprint);
                            log.Info($"Host key {algorithm} SHA256:{fingerprint} → {check}.");
                            return check;
                        }),
                    ct).ConfigureAwait(false);
                _openSession = session;
                run.LoginUsed = credential.SourceDescription;
                log.Info($"Logged in with {credential}.");
                break;
            }
            catch (SshAuthenticationFailedException ex)
            {
                log.Warn(ex.Message);
            }
            catch (SshHostKeyRejectedException ex)
            {
                log.Error(ex.Message);
                run.Notes.Add(ex.Message);
                run.Notes.Add("If the access point was factory reset, IT can clear saved fingerprints under IT settings.");
                return (AdoptionOutcome.HostKeyMismatch, null, null);
            }
            catch (SshUnavailableException ex)
            {
                log.Warn(ex.Message);
                run.Notes.Add(ex.Message);
                return (AdoptionOutcome.SshUnavailable, null, null);
            }
        }

        if (session is null)
        {
            if (run.DiscoverySaysFactoryDefault == false)
            {
                run.Notes.Add("The device's discovery reply indicates it is not in factory-default state, so the factory login is not expected to work.");
            }

            return (AdoptionOutcome.AuthenticationFailed, null, null);
        }

        // 6. Confirm identity and capabilities (read-only) ------------------------------------------
        Report(66, "Checking the access point…");
        var commandTimeout = TimeSpan.FromSeconds(settings.SshCommandTimeoutSeconds);
        var activeSession = session;

        async Task<CommandResult> Run(string command)
        {
            var result = await activeSession.RunAsync(command, commandTimeout, ct).ConfigureAwait(false);
            log.Info($"$ {command}  → exit {result.ExitStatus?.ToString() ?? "none"}{(result.TimedOut ? " (timed out)" : string.Empty)}\n{result.Combined.Trim()}");
            return result;
        }

        var hasMcaCliOp = await Run(DeviceCommands.FindMcaCliOp).ConfigureAwait(false) is { Succeeded: true } found &&
                          found.Output.Trim().Length > 0;

        DeviceInfo? info = null;
        if (hasMcaCliOp)
        {
            var infoResult = await Run(DeviceCommands.Info).ConfigureAwait(false);
            if (infoResult.Succeeded)
            {
                info = DeviceInfoParser.Parse(infoResult.Output);
                ApplyInfo(run, info);
            }
        }

        var reportedMacs = new List<MacAddress>();
        if (info?.Mac is { } infoMac)
        {
            reportedMacs.Add(infoMac);
        }
        else
        {
            reportedMacs.AddRange(DeviceInfoParser.ParseMacLines((await Run(DeviceCommands.ReadMacAddresses).ConfigureAwait(false)).Output));
        }

        if (reportedMacs.Count == 0)
        {
            run.Notes.Add("The device did not report its own MAC address, so its identity could not be confirmed.");
            return (AdoptionOutcome.DeviceIdentityMismatch, null, session);
        }

        if (!reportedMacs.Contains(device.Mac))
        {
            run.Notes.Add($"Expected {device.Mac}; the device reports {string.Join(", ", reportedMacs)}.");
            return (AdoptionOutcome.DeviceIdentityMismatch, null, session);
        }

        log.Info("The device confirmed the expected MAC address.");
        if (session.HostKeyAlgorithm is { } keyAlgorithm && session.HostKeyFingerprint is { } keyFingerprint)
        {
            deps.HostKeys.Remember(device.Mac, keyAlgorithm, keyFingerprint);
        }

        if (info?.Model is { } model &&
            !settings.SupportedModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            run.Notes.Add($"Model '{model}' does not start with any of: {string.Join(", ", settings.SupportedModelPrefixes)}.");
            return (AdoptionOutcome.UnsupportedDevice, null, session);
        }

        var useInteractive = false;
        if (!hasMcaCliOp)
        {
            var hasMcaCli = await Run(DeviceCommands.FindMcaCli).ConfigureAwait(false) is { Succeeded: true } cli &&
                            cli.Output.Trim().Length > 0;
            if (!hasMcaCli || !settings.AllowInteractiveMcaCliFallback)
            {
                run.Notes.Add(hasMcaCli
                    ? "mca-cli-op is not available and the interactive mca-cli fallback is disabled in settings."
                    : "Neither mca-cli-op nor mca-cli is available on this firmware.");
                return (AdoptionOutcome.UnsupportedDevice, null, session);
            }

            useInteractive = true;
            log.Warn("mca-cli-op not found; will use the interactive mca-cli session. The result cannot be read back automatically.");
            run.Notes.Add("Firmware lacks mca-cli-op; the legacy interactive mca-cli session was used and the status could not be read back.");
        }

        if (info is { State: DeviceInformState.Connected } && controller.Matches(info.StatusUrl))
        {
            log.Info("Already connected to the configured controller; no change needed.");
            run.Notes.Add("The device already reports Connected to the configured controller; a real run would change nothing.");
            return request.TestMode
                ? (AdoptionOutcome.DryRunCompleted, null, session)
                : (AdoptionOutcome.Connected, null, session);
        }

        // 7. Can the device resolve the controller name? ----------------------------------------------
        Report(72, "Checking that the access point can find the controller…");
        var resolution = controller.HostIsIpAddress
            ? new ResolveCheckResult(NameResolution.Resolved, IPAddress.Parse(controller.Host))
            : ResolveOutputParser.Parse((await Run(DeviceCommands.ResolveCheck(controller)).ConfigureAwait(false)).Combined);
        log.Info($"Controller name on the device: {resolution.State} {resolution.Address}");

        string? hostsCommand = null;
        if (resolution.State != NameResolution.Resolved && !string.IsNullOrWhiteSpace(settings.HostsFallbackControllerIp))
        {
            var fallbackIp = IPAddress.Parse(settings.HostsFallbackControllerIp);
            var entries = HostsFile.FindEntriesFor((await Run(DeviceCommands.ReadHostsFile).ConfigureAwait(false)).Output, controller.Host);
            foreach (var malformed in entries.Where(e => e.IsMalformed))
            {
                run.Notes.Add($"/etc/hosts has a malformed line that has no effect: '{malformed.Line}'. It was left unchanged.");
            }

            var valid = entries.Where(e => !e.IsMalformed).ToList();
            if (valid.Any(e => fallbackIp.Equals(e.Address)))
            {
                log.Info("A correct /etc/hosts entry already exists; not adding a duplicate.");
            }
            else if (valid.Count > 0)
            {
                run.Notes.Add($"/etc/hosts already maps {controller.Host} to {valid[0].Address}; it was left unchanged.");
            }
            else
            {
                hostsCommand = DeviceCommands.AppendHostsEntry(fallbackIp, controller);
                run.Planned.Add(hostsCommand);
            }
        }

        run.Planned.Add(useInteractive ? DeviceCommands.DescribeInteractiveSetInform(controller) : DeviceCommands.SetInform(controller));

        if (request.TestMode)
        {
            log.Info("Test mode: stopping before any change. Planned commands:\n  " + string.Join("\n  ", run.Planned));
            return (AdoptionOutcome.DryRunCompleted, null, session);
        }

        // 8. Change the device ----------------------------------------------------------------------
        if (hostsCommand is not null)
        {
            Report(76, "Telling the access point where the controller is…");
            run.DeviceChanged = true;
            var hostsResult = await Run(hostsCommand).ConfigureAwait(false);
            if (!hostsResult.Succeeded)
            {
                run.Notes.Add("Adding the /etc/hosts entry failed.");
            }

            resolution = ResolveOutputParser.Parse((await Run(DeviceCommands.ResolveCheck(controller)).ConfigureAwait(false)).Combined);
            log.Info($"Controller name on the device after /etc/hosts change: {resolution.State} {resolution.Address}");
            run.Notes.Add("An /etc/hosts entry was added on the access point. It is not kept after the access point restarts.");
        }

        Report(80, "Sending the controller address to the access point…");
        run.DeviceChanged = true;
        if (useInteractive)
        {
            var interactive = await session.RunInteractiveAsync(DeviceCommands.InteractiveSetInform(controller), commandTimeout, ct)
                .ConfigureAwait(false);
            log.Info($"Interactive mca-cli transcript:\n{interactive.Transcript.Trim()}");
            if (!DeviceCommands.InteractiveSetInformAccepted(interactive))
            {
                return (AdoptionOutcome.InformFailed, null, session);
            }

            return (resolution.State == NameResolution.NotResolved ? AdoptionOutcome.ControllerNameUnresolvable : AdoptionOutcome.InformSentNotVerified,
                null, session);
        }

        var informResult = await Run(DeviceCommands.SetInform(controller)).ConfigureAwait(false);
        if (!DeviceCommands.SetInformAccepted(informResult))
        {
            run.Notes.Add($"set-inform output: {informResult.Combined.Trim()}");
            return (AdoptionOutcome.InformFailed, null, session);
        }

        // 9. Read back the status ---------------------------------------------------------------------
        Report(84, "Waiting for the access point to contact the controller…");
        var deadline = DateTime.UtcNow.AddSeconds(settings.VerifySeconds);
        DeviceInfo? last = null;
        while (true)
        {
            var infoResult = await Run(DeviceCommands.Info).ConfigureAwait(false);
            if (infoResult.Succeeded)
            {
                last = DeviceInfoParser.Parse(infoResult.Output);
                ApplyInfo(run, last);
                if (last.State == DeviceInformState.Connected && controller.Matches(last.StatusUrl))
                {
                    break;
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var elapsedShare = 1 - (remaining.TotalSeconds / Math.Max(1, settings.VerifySeconds));
            Report(84 + (int)(elapsedShare * 14), "Waiting for the access point to contact the controller…");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(settings.VerifyPollSeconds, Math.Max(remaining.TotalSeconds, 0.1))), ct)
                .ConfigureAwait(false);
        }

        Report(99, "Finishing…");
        return (Classify(last, controller, resolution, run), null, session);
    }

    private AdoptionOutcome Classify(DeviceInfo? last, ControllerTarget controller, ResolveCheckResult resolution, RunState run)
    {
        if (last is null || last.StatusText is null)
        {
            run.Notes.Add("The device status could not be read after set-inform.");
            return AdoptionOutcome.InformSentNotVerified;
        }

        if (!controller.Matches(last.StatusUrl))
        {
            if (last.StatusUrl is null)
            {
                return AdoptionOutcome.InformSentNotVerified;
            }

            run.Notes.Add($"After set-inform the device still reports {last.StatusUrl} instead of {controller.InformUrl}.");
            return AdoptionOutcome.InformFailed;
        }

        return last.State switch
        {
            DeviceInformState.Connected => AdoptionOutcome.Connected,
            DeviceInformState.Rejected => AdoptionOutcome.ControllerRejected,
            DeviceInformState.Timeout or DeviceInformState.Disconnected => resolution.State == NameResolution.NotResolved
                ? AdoptionOutcome.ControllerNameUnresolvable
                : AdoptionOutcome.ControllerUnreachable,
            _ => resolution.State == NameResolution.NotResolved
                ? AdoptionOutcome.ControllerNameUnresolvable
                : AdoptionOutcome.PendingAdoption,
        };
    }

    private static void ApplyInfo(RunState run, DeviceInfo info)
    {
        run.Model = info.Model ?? run.Model;
        run.Firmware = info.Version ?? run.Firmware;
        run.StatusText = info.StatusText ?? run.StatusText;
        run.StatusUrl = info.StatusUrl ?? run.StatusUrl;
    }

    private List<SshCredential> BuildCredentialList(AdoptionRequest request)
    {
        var list = new List<SshCredential>();
        if (request.SessionCredential is { } sessionCredential)
        {
            list.Add(sessionCredential);
        }

        try
        {
            if (deps.SavedCredentials?.LoadSaved() is { } saved)
            {
                list.Add(saved);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"The saved login could not be read: {ex.GetType().Name}.");
        }

        foreach (var credential in list)
        {
            log.AddSecret(credential.Password);
        }

        if (settings.AllowFactoryDefaultCredentials)
        {
            list.Add(new SshCredential(settings.FactoryDefaultUsername, settings.FactoryDefaultPassword, CredentialSource.FactoryDefault));
        }

        return list.DistinctBy(c => (c.Username, c.Password)).Take(MaxLoginAttempts).ToList();
    }

    private AdoptionReport Finish(AdoptionOutcome outcome, RunState run, string? detail)
    {
        var (tone, title, message, steps) = UserMessages.For(outcome, run, settings.SupportContact, detail);
        var report = new AdoptionReport(
            outcome, tone, title, message, steps, run.DeviceChanged, run.Suffix, run.ToDeviceSummary(),
            outcome == AdoptionOutcome.DryRunCompleted ? run.Planned.ToList() : [],
            run.Notes.ToList(), DateTimeOffset.Now);
        log.Info($"--- Run finished: {outcome}. Device changed: {run.DeviceChanged}.");
        return report;
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
