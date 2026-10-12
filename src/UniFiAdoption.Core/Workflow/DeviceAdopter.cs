using System.Net;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Device;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Ssh;

namespace UniFiAdoption.Core.Workflow;

/// <summary>Result of logging in with the factory password during a read-only check.</summary>
public enum LoginResult
{
    NotTried,
    FactoryAccepted,
    FactoryRefused,
    HostKeyRejected,
    IdentityMismatch,
    Failed,
}

/// <summary>What a read-only check learned about one access point. Nothing on the device is changed.</summary>
public sealed record DeviceCheck(bool SshOpen, LoginResult Login, DeviceInfo? Info, string? Problem);

/// <summary>
/// The per-device steps used everywhere: the single-device flow, the live list's read-only checks and
/// batches. Each call opens its own SSH session and closes it before returning.
/// </summary>
public sealed class DeviceAdopter
{
    private const int MaxLoginAttempts = 3;
    private readonly AssistantSettings _settings;
    private readonly WorkflowDependencies _deps;
    private readonly DiagnosticLog _log;
    private readonly ControllerTarget? _controller;

    public DeviceAdopter(AssistantSettings settings, WorkflowDependencies deps, DiagnosticLog log)
    {
        _settings = settings;
        _deps = deps;
        _log = log;
        _controller = settings.Validate().Count == 0 && ControllerTarget.TryCreate(settings.InformUrl, out var target, out _) ? target : null;
    }

    public ControllerTarget? Controller => _controller;

    /// <summary>
    /// Read-only check used to sort the live list: is SSH open, does the factory password still work,
    /// and if so what does the access point report about itself. Only the public factory login is used.
    /// </summary>
    public async Task<DeviceCheck> CheckAsync(MacAddress mac, IPAddress address, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Min(_settings.SshConnectTimeoutSeconds, 8));
        var banner = await _deps.BannerProbe.ProbeAsync(address, _settings.SshPort, timeout, ct).ConfigureAwait(false);
        if (!banner.Reachable)
        {
            return new DeviceCheck(false, LoginResult.NotTried, null, banner.Problem);
        }

        if (!_settings.AllowFactoryDefaultCredentials)
        {
            return new DeviceCheck(true, LoginResult.NotTried, null, null);
        }

        var credential = FactoryCredential();
        var keyChanged = false;
        ISshSession session;
        try
        {
            session = await _deps.Ssh.ConnectAsync(
                new SshConnectRequest(address, _settings.SshPort, credential, TimeSpan.FromSeconds(_settings.SshConnectTimeoutSeconds),
                    HostKeyPolicy(mac, credential, () => keyChanged = true)),
                ct).ConfigureAwait(false);
        }
        catch (SshAuthenticationFailedException)
        {
            _log.Info($"Check {mac}: factory password refused (SSH open) → treated as adopted.");
            return new DeviceCheck(true, LoginResult.FactoryRefused, null, null);
        }
        catch (SshHostKeyRejectedException ex)
        {
            _log.Warn($"Check {mac}: {ex.Message}");
            return new DeviceCheck(true, LoginResult.HostKeyRejected, null, ex.Message);
        }
        catch (SshUnavailableException ex)
        {
            return new DeviceCheck(true, LoginResult.Failed, null, ex.Message);
        }

        using (session)
        {
            var commandTimeout = TimeSpan.FromSeconds(_settings.SshCommandTimeoutSeconds);
            var infoResult = await session.RunAsync(DeviceCommands.Info, commandTimeout, ct).ConfigureAwait(false);
            var info = infoResult.Succeeded ? DeviceInfoParser.Parse(infoResult.Output) : null;
            var reported = info?.Mac is { } infoMac
                ? new[] { infoMac }
                : DeviceInfoParser.ParseMacLines((await session.RunAsync(DeviceCommands.ReadMacAddresses, commandTimeout, ct).ConfigureAwait(false)).Output);

            if (!reported.Contains(mac))
            {
                _log.Warn($"Check {mac}: device reports {string.Join(", ", reported)}.");
                return new DeviceCheck(true, LoginResult.IdentityMismatch, null, "The device reported a different MAC address.");
            }

            RememberKey(mac, session, keyChanged);
            _log.Info($"Check {mac}: factory password accepted. Model {info?.Model ?? "?"}, status '{info?.StatusText ?? "?"}' ({info?.StatusUrl ?? "no URL"}).");
            return new DeviceCheck(true, LoginResult.FactoryAccepted, info, null);
        }
    }

    /// <summary>
    /// Points one verified access point at the controller (or, in test mode, shows what would be done).
    /// Every safety check from the single-device flow applies.
    /// </summary>
    internal async Task<(AdoptionOutcome Outcome, string? Detail)> AdoptAsync(
        RunState run, MacAddress mac, IPAddress address, bool testMode, SshCredential? sessionCredential,
        int verifySeconds, Action<int, string>? report, CancellationToken ct)
    {
        void Report(int percent, string message) => report?.Invoke(percent, message);

        if (_controller is null)
        {
            return (AdoptionOutcome.ConfigurationError, null);
        }

        var controller = _controller;

        // SSH available? (no credentials sent)
        Report(58, "Connecting…");
        var banner = await _deps.BannerProbe.ProbeAsync(address, _settings.SshPort, TimeSpan.FromSeconds(_settings.SshConnectTimeoutSeconds), ct)
            .ConfigureAwait(false);
        if (!banner.Reachable)
        {
            _log.Warn($"SSH not available on {address}:{_settings.SshPort}: {banner.Problem}");
            run.Notes.Add($"SSH check: {banner.Problem}");
            return (AdoptionOutcome.SshUnavailable, null);
        }

        _log.Info($"SSH server identifies as: {banner.Banner}");

        // Log in
        var credentials = BuildCredentialList(sessionCredential);
        if (credentials.Count == 0)
        {
            run.NoLoginAvailable = true;
            _log.Warn("No login available: factory login disabled and none entered in Admin settings.");
            return (AdoptionOutcome.AuthenticationFailed, null);
        }

        ISshSession? session = null;
        var keyChanged = false;
        var factoryRefused = false;
        foreach (var credential in credentials)
        {
            ct.ThrowIfCancellationRequested();
            _log.Info($"Trying {credential}.");
            try
            {
                session = await _deps.Ssh.ConnectAsync(
                    new SshConnectRequest(address, _settings.SshPort, credential, TimeSpan.FromSeconds(_settings.SshConnectTimeoutSeconds),
                        HostKeyPolicy(mac, credential, () => keyChanged = true)),
                    ct).ConfigureAwait(false);
                run.LoginUsed = credential.SourceDescription;
                _log.Info($"Logged in with {credential}.");
                break;
            }
            catch (SshAuthenticationFailedException ex)
            {
                factoryRefused |= credential.Source == CredentialSource.FactoryDefault;
                _log.Warn(ex.Message);
            }
            catch (SshHostKeyRejectedException ex)
            {
                _log.Error(ex.Message);
                run.Notes.Add(ex.Message);
                run.Notes.Add("If the access point was factory reset, saved fingerprints can be cleared under Admin settings.");
                return (AdoptionOutcome.HostKeyMismatch, null);
            }
            catch (SshUnavailableException ex)
            {
                _log.Warn(ex.Message);
                run.Notes.Add(ex.Message);
                return (AdoptionOutcome.SshUnavailable, null);
            }
        }

        if (session is null)
        {
            // Team rule: SSH open but the factory password refused means the controller has adopted it.
            return (factoryRefused ? AdoptionOutcome.AlreadyAdopted : AdoptionOutcome.AuthenticationFailed, null);
        }

        using (session)
        {
            return await AdoptWithSessionAsync(run, mac, session, keyChanged, controller, testMode, verifySeconds, Report, ct).ConfigureAwait(false);
        }
    }

    private async Task<(AdoptionOutcome Outcome, string? Detail)> AdoptWithSessionAsync(
        RunState run, MacAddress mac, ISshSession session, bool keyChanged, ControllerTarget controller,
        bool testMode, int verifySeconds, Action<int, string> report, CancellationToken ct)
    {
        report(66, "Checking the access point…");
        var commandTimeout = TimeSpan.FromSeconds(_settings.SshCommandTimeoutSeconds);

        async Task<CommandResult> Run(string command)
        {
            var result = await session.RunAsync(command, commandTimeout, ct).ConfigureAwait(false);
            _log.Info($"$ {command}  → exit {result.ExitStatus?.ToString() ?? "none"}{(result.TimedOut ? " (timed out)" : string.Empty)}\n{result.Combined.Trim()}");
            return result;
        }

        var found = await Run(DeviceCommands.FindMcaCliOp).ConfigureAwait(false);
        var hasMcaCliOp = found.Succeeded && found.Output.Trim().Length > 0;

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
            return (AdoptionOutcome.DeviceIdentityMismatch, null);
        }

        if (!reportedMacs.Contains(mac))
        {
            run.Notes.Add($"Expected {mac}; the device reports {string.Join(", ", reportedMacs)}.");
            return (AdoptionOutcome.DeviceIdentityMismatch, null);
        }

        _log.Info("The device confirmed the expected MAC address.");
        RememberKey(mac, session, keyChanged);

        if (info?.Model is { } model &&
            !_settings.SupportedModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            run.Notes.Add($"Model '{model}' does not start with any of: {string.Join(", ", _settings.SupportedModelPrefixes)}.");
            return (AdoptionOutcome.UnsupportedDevice, null);
        }

        var useInteractive = false;
        if (!hasMcaCliOp)
        {
            var cli = await Run(DeviceCommands.FindMcaCli).ConfigureAwait(false);
            var hasMcaCli = cli.Succeeded && cli.Output.Trim().Length > 0;
            if (!hasMcaCli || !_settings.AllowInteractiveMcaCliFallback)
            {
                run.Notes.Add(hasMcaCli
                    ? "mca-cli-op is not available and the interactive mca-cli fallback is disabled in settings."
                    : "Neither mca-cli-op nor mca-cli is available on this firmware.");
                return (AdoptionOutcome.UnsupportedDevice, null);
            }

            useInteractive = true;
            _log.Warn("mca-cli-op not found; will use the interactive mca-cli session. The result cannot be read back automatically.");
            run.Notes.Add("Firmware lacks mca-cli-op; the legacy interactive mca-cli session was used and the status could not be read back.");
        }

        if (info is { State: DeviceInformState.Connected } && controller.Matches(info.StatusUrl))
        {
            _log.Info("Already connected to the configured controller; no change needed.");
            run.Notes.Add("The device already reports Connected to the configured controller; a real run would change nothing.");
            return (testMode ? AdoptionOutcome.DryRunCompleted : AdoptionOutcome.Connected, null);
        }

        // Can the device resolve the controller name?
        report(72, "Checking that it can find the controller…");
        var resolution = controller.HostIsIpAddress
            ? new ResolveCheckResult(NameResolution.Resolved, IPAddress.Parse(controller.Host))
            : ResolveOutputParser.Parse((await Run(DeviceCommands.ResolveCheck(controller)).ConfigureAwait(false)).Combined);
        _log.Info($"Controller name on the device: {resolution.State} {resolution.Address}");

        string? hostsCommand = null;
        if (resolution.State != NameResolution.Resolved && !string.IsNullOrWhiteSpace(_settings.HostsFallbackControllerIp))
        {
            var fallbackIp = IPAddress.Parse(_settings.HostsFallbackControllerIp);
            var entries = HostsFile.FindEntriesFor((await Run(DeviceCommands.ReadHostsFile).ConfigureAwait(false)).Output, controller.Host);
            foreach (var malformed in entries.Where(e => e.IsMalformed))
            {
                run.Notes.Add($"/etc/hosts has a malformed line that has no effect: '{malformed.Line}'. It was left unchanged.");
            }

            var valid = entries.Where(e => !e.IsMalformed).ToList();
            if (valid.Any(e => fallbackIp.Equals(e.Address)))
            {
                _log.Info("A correct /etc/hosts entry already exists; not adding a duplicate.");
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
        if (testMode)
        {
            _log.Info("Test mode: stopping before any change. Planned commands:\n  " + string.Join("\n  ", run.Planned));
            return (AdoptionOutcome.DryRunCompleted, null);
        }

        // Change the device
        if (hostsCommand is not null)
        {
            report(76, "Telling it where the controller is…");
            run.DeviceChanged = true;
            if (!(await Run(hostsCommand).ConfigureAwait(false)).Succeeded)
            {
                run.Notes.Add("Adding the /etc/hosts entry failed.");
            }

            resolution = ResolveOutputParser.Parse((await Run(DeviceCommands.ResolveCheck(controller)).ConfigureAwait(false)).Combined);
            run.Notes.Add("An /etc/hosts entry was added on the access point. It is not kept after the access point restarts.");
        }

        report(80, "Sending the controller address…");
        run.DeviceChanged = true;
        if (useInteractive)
        {
            var interactive = await session.RunInteractiveAsync(DeviceCommands.InteractiveSetInform(controller), commandTimeout, ct).ConfigureAwait(false);
            _log.Info($"Interactive mca-cli transcript:\n{interactive.Transcript.Trim()}");
            if (!DeviceCommands.InteractiveSetInformAccepted(interactive))
            {
                return (AdoptionOutcome.InformFailed, null);
            }

            run.StatusUrl = controller.InformUrl;
            return (resolution.State == NameResolution.NotResolved ? AdoptionOutcome.ControllerNameUnresolvable : AdoptionOutcome.InformSentNotVerified, null);
        }

        var informResult = await Run(DeviceCommands.SetInform(controller)).ConfigureAwait(false);
        if (!DeviceCommands.SetInformAccepted(informResult))
        {
            run.Notes.Add($"set-inform output: {informResult.Combined.Trim()}");
            return (AdoptionOutcome.InformFailed, null);
        }

        // Read back the status
        report(84, "Checking that it took the address…");
        var deadline = DateTime.UtcNow.AddSeconds(verifySeconds);
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

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(_settings.VerifyPollSeconds, Math.Max(remaining.TotalSeconds, 0.1))), ct)
                .ConfigureAwait(false);
        }

        report(99, "Finishing…");
        return (Classify(last, controller, resolution, run), null);
    }

    private static AdoptionOutcome Classify(DeviceInfo? last, ControllerTarget controller, ResolveCheckResult resolution, RunState run)
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

    /// <summary>
    /// Host-key rule: a key pinned by IT, or any change when an IT-entered password would be sent, is
    /// refused. A change seen with the public factory password is accepted (factory-reset access points get
    /// new keys, and that password is no secret), then re-recorded once the device confirms its MAC.
    /// </summary>
    private Func<string, string, HostKeyCheck> HostKeyPolicy(MacAddress mac, SshCredential credential, Action onKeyChanged) =>
        (algorithm, fingerprint) =>
        {
            var check = _deps.HostKeys.Check(mac, algorithm, fingerprint);
            if (check == HostKeyCheck.Rejected && credential.Source == CredentialSource.FactoryDefault && !_deps.HostKeys.IsPinned(mac))
            {
                _log.Warn($"Host key for {mac} changed ({algorithm} SHA256:{fingerprint}); accepted for the factory login only (expected after a factory reset).");
                onKeyChanged();
                return HostKeyCheck.TrustedFirstUse;
            }

            _log.Info($"Host key {algorithm} SHA256:{fingerprint} → {check}.");
            return check;
        };

    private void RememberKey(MacAddress mac, ISshSession session, bool replace)
    {
        if (session.HostKeyAlgorithm is { } algorithm && session.HostKeyFingerprint is { } fingerprint)
        {
            _deps.HostKeys.Remember(mac, algorithm, fingerprint, replace);
        }
    }

    private SshCredential FactoryCredential() =>
        new(_settings.FactoryDefaultUsername, _settings.FactoryDefaultPassword, CredentialSource.FactoryDefault);

    private List<SshCredential> BuildCredentialList(SshCredential? sessionCredential)
    {
        var list = new List<SshCredential>();
        if (sessionCredential is not null)
        {
            list.Add(sessionCredential);
        }

        try
        {
            if (_deps.SavedCredentials?.LoadSaved() is { } saved)
            {
                list.Add(saved);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"The saved login could not be read: {ex.GetType().Name}.");
        }

        foreach (var credential in list)
        {
            _log.AddSecret(credential.Password);
        }

        if (_settings.AllowFactoryDefaultCredentials)
        {
            list.Add(FactoryCredential());
        }

        return list.DistinctByKey(c => c.Username + "\u0000" + c.Password).Take(MaxLoginAttempts).ToList();
    }

    internal static void ApplyInfo(RunState run, DeviceInfo info)
    {
        run.Model = info.Model ?? run.Model;
        run.Firmware = info.Version ?? run.Firmware;
        run.StatusText = info.StatusText ?? run.StatusText;
        run.StatusUrl = info.StatusUrl ?? run.StatusUrl;
    }
}
