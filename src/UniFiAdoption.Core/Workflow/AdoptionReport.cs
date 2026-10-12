using System.Globalization;
using System.Reflection;
using System.Text;

namespace UniFiAdoption.Core.Workflow;

public enum AdoptionOutcome
{
    // The device reports it is connected to the configured controller.
    Connected,

    // set-inform was accepted and the device points at the controller; IT must still approve it.
    PendingAdoption,

    // set-inform was accepted but the device's status could not be read back.
    InformSentNotVerified,

    // The device points at the controller but reports it cannot reach it.
    ControllerUnreachable,

    // The device points at the controller but cannot resolve the controller's name.
    ControllerNameUnresolvable,

    ControllerRejected,
    DryRunCompleted,
    InvalidInput,
    ConfigurationError,
    NoNetwork,
    PermissionDenied,
    NotFound,
    MultipleMatches,
    AmbiguousAddress,
    NotUnifiDevice,
    OnOtherNetwork,
    SshUnavailable,
    AuthenticationFailed,
    HostKeyMismatch,
    DeviceIdentityMismatch,
    UnsupportedDevice,
    InformFailed,
    Cancelled,
    UnexpectedError,
}

public enum ResultTone
{
    Success,
    Waiting,
    Problem,
    Info,
}

public sealed record DeviceSummary(
    string Mac,
    string? IpAddress,
    string? Model,
    string? Firmware,
    string? StatusText,
    string? StatusUrl,
    string? LoginUsed);

public sealed record WorkflowProgress(int Percent, string Message);

public sealed record AdoptionReport(
    AdoptionOutcome Outcome,
    ResultTone Tone,
    string Title,
    string Message,
    IReadOnlyList<string> NextSteps,
    bool DeviceWasChanged,
    string? EnteredSuffix,
    DeviceSummary? Device,
    IReadOnlyList<string> PlannedCommands,
    IReadOnlyList<string> TechnicalNotes,
    DateTimeOffset FinishedAt)
{
    public static string AssistantVersion =>
        typeof(AdoptionReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "unknown";

    /// <summary>A summary for IT, safe to paste into chat or email (contains no passwords).</summary>
    public string ToSupportText(string? logFilePath)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"UniFi Adoption Assistant {AssistantVersion}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Time: {FinishedAt:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Computer: {Environment.MachineName}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Result: {Outcome} – {Title}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Characters entered: {EnteredSuffix ?? "(none)"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Access point changed: {(DeviceWasChanged ? "yes" : "no")}");
        if (Device is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Device MAC: {Device.Mac}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Device IP: {Device.IpAddress ?? "unknown"}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Model: {Device.Model ?? "unknown"}   Firmware: {Device.Firmware ?? "unknown"}");
            if (Device.StatusText is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Device status: {Device.StatusText} ({Device.StatusUrl ?? "no URL"})");
            }

            if (Device.LoginUsed is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Login used: {Device.LoginUsed}");
            }
        }

        if (PlannedCommands.Count > 0)
        {
            text.AppendLine("Commands that would be run (test mode):");
            foreach (var command in PlannedCommands)
            {
                text.AppendLine("  " + command);
            }
        }

        if (TechnicalNotes.Count > 0)
        {
            text.AppendLine("Notes:");
            foreach (var note in TechnicalNotes)
            {
                text.AppendLine("  - " + note);
            }
        }

        if (logFilePath is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Log file: {logFilePath}");
        }

        return text.ToString();
    }
}

/// <summary>Everything the workflow learned during one run; turned into a report at the end.</summary>
internal sealed class RunState
{
    public string? Suffix { get; set; }

    public bool DeviceChanged { get; set; }

    public int DevicesSeen { get; set; }

    public int MatchCount { get; set; }

    public bool OnlySelfAssignedAddress { get; set; }

    public string? Mac { get; set; }

    public string? IpAddress { get; set; }

    public string? Model { get; set; }

    public string? Firmware { get; set; }

    public string? StatusText { get; set; }

    public string? StatusUrl { get; set; }

    public string? LoginUsed { get; set; }

    public bool NoLoginAvailable { get; set; }

    public bool? DiscoverySaysFactoryDefault { get; set; }

    public ControllerReachability? FromThisComputer { get; set; }

    public List<string> Planned { get; } = [];

    public List<string> Notes { get; } = [];

    public DeviceSummary? ToDeviceSummary() =>
        Mac is null ? null : new DeviceSummary(Mac, IpAddress, Model, Firmware, StatusText, StatusUrl, LoginUsed);
}

/// <summary>Plain-language wording for each outcome, written for people without networking knowledge.</summary>
internal static class UserMessages
{
    public static (ResultTone Tone, string Title, string Message, List<string> Steps) For(
        AdoptionOutcome outcome, RunState run, string support, string? detail)
    {
        var contact = $"Contact {support}";
        var contactLower = $"contact {support}";
        var contactWithDetails = $"Contact {support} and send them the details (use “Copy details for IT”).";
        var nothingChanged = "Nothing was changed on the access point.";
        var pcCannotReachController = run.FromThisComputer is { PortReachable: false }
            ? " This computer can't reach the controller either, so the internet connection at this site may not be working."
            : string.Empty;

        switch (outcome)
        {
            case AdoptionOutcome.Connected:
                return run.DeviceChanged
                    ? (ResultTone.Success, "Access point connected",
                        "The access point reports that it is connected to the controller. You don't need to do anything else.",
                        ["Leave the access point plugged in."])
                    : (ResultTone.Success, "Already connected",
                        "This access point already reports that it is connected to the controller, so nothing was changed.",
                        []);

            case AdoptionOutcome.PendingAdoption:
                return (ResultTone.Waiting, "Done here – waiting for IT to approve it",
                    "The access point now has the controller's address. It will start working after IT approves it in the controller.",
                    ["Leave the access point plugged in and switched on.", $"Let {support} know it is ready (use “Copy details for IT”)."]);

            case AdoptionOutcome.InformSentNotVerified:
                return (ResultTone.Waiting, "Sent – IT needs to check it",
                    "The access point accepted the controller's address, but this assistant couldn't confirm what happened next.",
                    ["Leave the access point plugged in.", $"Ask {support} to check that it appears in the controller."]);

            case AdoptionOutcome.ControllerUnreachable:
                return (ResultTone.Waiting, "Set up, but it can't reach the controller yet",
                    "The access point has the controller's address, but it can't connect to the controller right now." + pcCannotReachController,
                    [
                        "Check that the internet works at this site, for example by opening a website on this computer.",
                        "Leave the access point plugged in. It keeps trying by itself.",
                        $"If it still isn't working in an hour, {contactLower}.",
                    ]);

            case AdoptionOutcome.ControllerNameUnresolvable:
                return (ResultTone.Waiting, "Set up, but it can't find the controller yet",
                    "The access point has the controller's address, but the network at this site can't look up the controller's name. " +
                    "This usually means the internet connection or the router isn't working properly." + pcCannotReachController,
                    [
                        "Check that the internet works at this site, for example by opening a website on this computer.",
                        "Leave the access point plugged in. It keeps trying by itself.",
                        contactWithDetails,
                    ]);

            case AdoptionOutcome.ControllerRejected:
                return (ResultTone.Problem, "The controller refused the access point",
                    "The access point reached the controller, but the controller did not accept it. IT needs to look at this in the controller.",
                    [contactWithDetails]);

            case AdoptionOutcome.DryRunCompleted:
                return (ResultTone.Info, "Test finished – nothing was changed",
                    "Test mode found the access point and checked it. The details show what a real run would do.",
                    []);

            case AdoptionOutcome.InvalidInput:
                return (ResultTone.Problem, "Check the characters", detail ?? "Type the last 4 characters of the MAC address.",
                    ["Look at the label on the back of the access point.", "Type the last 4 characters of the MAC address."]);

            case AdoptionOutcome.ConfigurationError:
                return (ResultTone.Problem, "This copy of the assistant isn't set up correctly",
                    $"{nothingChanged} {support} needs to fix the assistant's settings.",
                    [contactWithDetails]);

            case AdoptionOutcome.NoNetwork:
                return (ResultTone.Problem, "This computer isn't connected to the network",
                    run.OnlySelfAssignedAddress
                        ? "This computer is plugged in, but it didn't get a network address. The router may be switched off, or a cable may be loose."
                        : "The assistant needs this computer to be on the same network as the access point.",
                    [
                        "Connect this computer to the office network with a cable or the office Wi-Fi.",
                        "Check that the router is switched on.",
                        "Try again.",
                    ]);

            case AdoptionOutcome.PermissionDenied:
                return (ResultTone.Problem, "This computer blocked the search",
                    $"Security settings on this computer stopped the assistant from searching the network. {nothingChanged}",
                    [contactWithDetails]);

            case AdoptionOutcome.NotFound:
                var notFoundSteps = new List<string>
                {
                    "Check the 4 characters on the label again.",
                    "Check that the light on the access point is on. If it isn't, check its power.",
                    "The access point gets its power through its network cable. The cable from the access point should go into the port marked POE on its small power adapter, and another cable should go from the LAN port to the router or network switch.",
                    "If the access point was only just plugged in, wait 2 minutes and try again.",
                };
                notFoundSteps.Add(run.DevicesSeen == 0
                    ? "This computer couldn't see any other devices on the network. Connect it to the office network with a cable, not a guest Wi-Fi network."
                    : "Make sure this computer is on the office network, not a guest Wi-Fi network.");
                return (ResultTone.Problem, "Access point not found",
                    $"No access point ending in {run.Suffix} answered on this network. {nothingChanged}",
                    notFoundSteps);

            case AdoptionOutcome.MultipleMatches:
                return (ResultTone.Problem, "More than one device matches",
                    $"{run.MatchCount} devices on this network end in {run.Suffix}. To avoid changing the wrong one, nothing was changed.",
                    [$"{contact} and tell them the 4 characters you entered."]);

            case AdoptionOutcome.AmbiguousAddress:
                return (ResultTone.Problem, "The access point has more than one address",
                    $"The access point answered from more than one network address, so the assistant couldn't be sure which one to use. {nothingChanged}",
                    [contactWithDetails]);

            case AdoptionOutcome.NotUnifiDevice:
                return (ResultTone.Problem, "That device doesn't look like a UniFi access point",
                    $"A device ending in {run.Suffix} answered, but it doesn't look like a UniFi access point, so nothing was sent to it.",
                    ["Check the 4 characters on the label again.", $"If they are correct, {contactLower}."]);

            case AdoptionOutcome.OnOtherNetwork:
                return (ResultTone.Problem, "The access point is on a different network",
                    $"The access point answered, but its address ({run.IpAddress ?? "unknown"}) can't be reached from this computer. " +
                    "This usually means it didn't get an address from the router.",
                    [
                        "Unplug the access point's power adapter for 10 seconds, then plug it back in.",
                        "Wait 2 minutes, then try again.",
                        $"If this keeps happening, {contactLower}.",
                    ]);

            case AdoptionOutcome.SshUnavailable:
                return (ResultTone.Problem, "The access point won't accept the setup connection",
                    $"The access point was found, but it didn't accept a setup connection. {nothingChanged}",
                    [
                        "Wait 2 minutes, because it may still be starting up, then try again.",
                        $"If this keeps happening, {contactLower}.",
                    ]);

            case AdoptionOutcome.AuthenticationFailed:
                var authMessage = run.NoLoginAvailable
                    ? $"The assistant has no login it is allowed to try on this access point. {nothingChanged}"
                    : $"The access point didn't accept the setup login. This usually means it was set up before and no longer uses its factory login. {nothingChanged}";
                return (ResultTone.Problem, "The access point didn't accept the login", authMessage,
                    [
                        $"{contact}. They can enter the right login under IT settings.",
                        $"Don't reset the access point unless {support} asks you to.",
                    ]);

            case AdoptionOutcome.HostKeyMismatch:
                return (ResultTone.Problem, "Security check failed",
                    "This access point's security fingerprint is different from the last time this computer set it up. " +
                    $"That is normal after a factory reset, but it can also mean something is wrong. {nothingChanged}",
                    [contactWithDetails]);

            case AdoptionOutcome.DeviceIdentityMismatch:
                return (ResultTone.Problem, "The device didn't match",
                    $"The device that answered didn't confirm the MAC address you entered, so nothing was changed.",
                    ["Check the 4 characters and try again.", $"If this keeps happening, {contactLower}."]);

            case AdoptionOutcome.UnsupportedDevice:
                return (ResultTone.Problem, "This access point can't be set up automatically",
                    $"Its model or software version doesn't support the setup commands this assistant uses. {nothingChanged}",
                    [contactWithDetails]);

            case AdoptionOutcome.InformFailed:
                return (ResultTone.Problem, "The access point didn't take the controller address",
                    "The assistant sent the controller's address, but the access point didn't confirm it.",
                    ["Try again.", $"If it fails again, {contactLower} and send them the details."]);

            case AdoptionOutcome.Cancelled:
                return (ResultTone.Info, "Stopped",
                    run.DeviceChanged
                        ? "The setup was stopped partway through. Run it again to finish."
                        : $"The setup was stopped. {nothingChanged}",
                    []);

            default:
                return (ResultTone.Problem, "Something went wrong",
                    run.DeviceChanged
                        ? "An unexpected problem stopped the setup. It may have been partly completed."
                        : $"An unexpected problem stopped the setup. {nothingChanged}",
                    ["Try again.", contactWithDetails]);
        }
    }
}
