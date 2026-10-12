using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;
using UniFiAdoption.Core.Ssh;

namespace UniFiAdoption.Core.Device;

/// <summary>
/// The controller inform URL after strict validation. Only this validated value is ever placed
/// into a device command, so nothing typed in the user interface can reach the device shell.
/// </summary>
public sealed record ControllerTarget(string InformUrl, string Host, int Port, bool HostIsIpAddress)
{
    private static readonly Regex HostnamePattern = new(
        @"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex SafeUrlCharacters = new(@"^[A-Za-z0-9.:/-]+$", RegexOptions.CultureInvariant);

    public static bool TryCreate(string? informUrl, [NotNullWhen(true)] out ControllerTarget? target, out string? problem)
    {
        target = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(informUrl) || !Uri.TryCreate(informUrl!.Trim(), UriKind.Absolute, out var uri))
        {
            problem = "InformUrl is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            problem = "InformUrl must start with http:// or https://.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            problem = "InformUrl must not contain a user name, query string or fragment.";
            return false;
        }

        if (!string.Equals(uri.AbsolutePath, "/inform", StringComparison.Ordinal))
        {
            problem = "InformUrl must end with /inform.";
            return false;
        }

        var isIp = uri.HostNameType == UriHostNameType.IPv4;
        var host = uri.IdnHost;
        if (!isIp && !HostnamePattern.IsMatch(host))
        {
            problem = "InformUrl contains an invalid host name.";
            return false;
        }

        var canonical = uri.IsDefaultPort ? $"{uri.Scheme}://{host}/inform" : $"{uri.Scheme}://{host}:{uri.Port}/inform";
        if (!SafeUrlCharacters.IsMatch(canonical))
        {
            problem = "InformUrl contains characters that are not allowed.";
            return false;
        }

        target = new ControllerTarget(canonical, host, uri.Port, isIp);
        return true;
    }

    public bool Matches(string? url) =>
        url is not null &&
        string.Equals(url.Trim().TrimEnd('/'), InformUrl, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Every command this application can send to an access point. All are fixed strings or are
/// built from validated values and single-quoted for the BusyBox shell.
/// </summary>
public static class DeviceCommands
{
    /// <summary>
    /// <c>mca-cli-op</c> is the non-interactive form of the UniFi device CLI. The legacy procedure
    /// (<c>mca-cli</c> then <c>set-inform</c>) starts an interactive program, which does not work in
    /// a non-interactive SSH exec channel, so this is used whenever the device provides it.
    /// </summary>
    public const string FindMcaCliOp = "command -v mca-cli-op 2>/dev/null || which mca-cli-op 2>/dev/null";

    public const string FindMcaCli = "command -v mca-cli 2>/dev/null || which mca-cli 2>/dev/null";

    /// <summary>Read-only: model, firmware version, MAC address and inform status.</summary>
    public const string Info = "mca-cli-op info";

    /// <summary>Read-only identity fallback for firmware without mca-cli-op.</summary>
    public const string ReadMacAddresses = "cat /sys/class/net/eth0/address 2>/dev/null; cat /sys/class/net/br0/address 2>/dev/null";

    public const string ReadHostsFile = "cat /etc/hosts";

    private static readonly Regex ErrorMarkers = new(
        @"error|not found|usage:|invalid|unknown command|failed|denied",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex InformAccepted = new(@"adoption request sent", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Read-only: asks the device's own resolver for the controller name (BusyBox ping prints the address it resolved).</summary>
    public static string ResolveCheck(ControllerTarget controller) => $"ping -c 1 -W 2 {ShellQuote(controller.Host)} 2>&1";

    public static string SetInform(ControllerTarget controller) => $"mca-cli-op set-inform {ShellQuote(controller.InformUrl)}";

    /// <summary>Appends one correctly formatted line: "&lt;IPv4&gt; &lt;hostname&gt;".</summary>
    public static string AppendHostsEntry(IPAddress address, ControllerTarget controller)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        }

        return $"printf '%s\\n' {ShellQuote($"{address} {controller.Host}")} >> /etc/hosts";
    }

    /// <summary>The legacy interactive sequence, used only when mca-cli-op is missing.</summary>
    public static IReadOnlyList<InteractiveStep> InteractiveSetInform(ControllerTarget controller) => new[]
    {
        new InteractiveStep("mca-cli", new Regex(@"[#>]\s*$", RegexOptions.CultureInvariant)),
        new InteractiveStep(
            $"set-inform {controller.InformUrl}",
            new Regex(@"adoption request sent|error|invalid|unknown|not found", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        new InteractiveStep("exit", new Regex(@"[#$>]\s*$", RegexOptions.CultureInvariant)),
    };

    public static string DescribeInteractiveSetInform(ControllerTarget controller) =>
        $"mca-cli  (interactive)  then  set-inform {controller.InformUrl}  then  exit";

    /// <summary>Single-quotes a value for a POSIX shell.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Decides whether set-inform was accepted. The confirmation text is decisive; otherwise a zero
    /// exit status with no error text is accepted. The status check afterwards is what really counts.
    /// </summary>
    public static bool SetInformAccepted(CommandResult result)
    {
        if (InformAccepted.IsMatch(result.Combined))
        {
            return true;
        }

        return result.Succeeded && !ErrorMarkers.IsMatch(result.Combined);
    }

    public static bool InteractiveSetInformAccepted(InteractiveResult result) =>
        result.StepsCompleted >= 2 && InformAccepted.IsMatch(result.Transcript);
}
