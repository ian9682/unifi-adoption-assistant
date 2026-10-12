using System.Net;
using System.Text.RegularExpressions;
using UniFiAdoption.Core.Networking;

namespace UniFiAdoption.Core.Device;

public enum DeviceInformState
{
    Unrecognized,
    NotAdopted,
    InProgress,
    Connected,
    Timeout,
    Rejected,
    Disconnected,
}

/// <summary>The output of <c>mca-cli-op info</c> (the same text as the <c>info</c> shell command).</summary>
public sealed record DeviceInfo(
    string? Model,
    string? Version,
    MacAddress? Mac,
    string? IpAddress,
    string? Hostname,
    string? StatusText,
    string? StatusUrl,
    DeviceInformState State,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// Parses "Key: value" lines such as
/// <code>
/// Model:       UAP-AC-Pro-Gen2
/// Version:     6.6.77.15402
/// MAC Address: 80:2a:a8:12:ab:cd
/// Status:      Not Adopted (http://unifi:8080/inform)
/// </code>
/// Status wording varies between firmware versions, so classification is by keyword and the raw
/// text is always kept for the log.
/// </summary>
public static class DeviceInfoParser
{
    private static readonly Regex LinePattern = new(
        @"^\s*(?<key>[A-Za-z][A-Za-z0-9 /]*?)\s*:\s*(?<value>.*?)\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex StatusPattern = new(
        @"^(?<state>[^(]*?)\s*(?:\((?<url>[^)]*)\))?\s*$", RegexOptions.CultureInvariant);

    public static DeviceInfo Parse(string output)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split('\n'))
        {
            var match = LinePattern.Match(rawLine.TrimEnd('\r'));
            if (match.Success)
            {
                fields.TryAdd(match.Groups["key"].Value.Trim(), match.Groups["value"].Value);
            }
        }

        string? Field(string key) => fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        MacAddress? mac = MacAddress.TryParse(Field("MAC Address") ?? Field("MAC"), out var parsedMac) ? parsedMac : null;

        string? statusText = null, statusUrl = null;
        var state = DeviceInformState.Unrecognized;
        if (Field("Status") is { } status)
        {
            var statusMatch = StatusPattern.Match(status);
            statusText = statusMatch.Success ? statusMatch.Groups["state"].Value.Trim() : status;
            statusUrl = statusMatch.Success && statusMatch.Groups["url"].Success ? statusMatch.Groups["url"].Value.Trim() : null;
            state = Classify(statusText);
        }

        return new DeviceInfo(Field("Model"), Field("Version"), mac, Field("IP Address"), Field("Hostname"), statusText, statusUrl, state, fields);
    }

    public static DeviceInformState Classify(string? statusText)
    {
        var s = (statusText ?? string.Empty).ToLowerInvariant();
        if (s.Contains("disconnect"))
        {
            return DeviceInformState.Disconnected;
        }

        if (s.Contains("connected"))
        {
            return DeviceInformState.Connected;
        }

        if (s.Contains("timeout") || s.Contains("timed out"))
        {
            return DeviceInformState.Timeout;
        }

        if (s.Contains("reject"))
        {
            return DeviceInformState.Rejected;
        }

        if (s.Contains("not adopted"))
        {
            return DeviceInformState.NotAdopted;
        }

        string[] inProgress = ["adopting", "unknown", "provision", "pending", "upgrading", "managed by other"];
        return inProgress.Any(s.Contains) ? DeviceInformState.InProgress : DeviceInformState.Unrecognized;
    }

    /// <summary>Reads MAC addresses from output that has one address per line (the /sys/class/net fallback).</summary>
    public static IReadOnlyList<MacAddress> ParseMacLines(string output) =>
        output.Split('\n')
            .Select(l => MacAddress.TryParse(l.Trim(), out var m) ? (MacAddress?)m : null)
            .Where(m => m is { IsUsableUnicast: true })
            .Select(m => m!.Value)
            .Distinct()
            .ToList();
}

public enum NameResolution
{
    Resolved,
    NotResolved,
    Unknown,
}

public sealed record ResolveCheckResult(NameResolution State, IPAddress? Address);

/// <summary>Interprets the output of <c>ping -c 1 host</c> purely for name resolution, not reachability.</summary>
public static class ResolveOutputParser
{
    private static readonly Regex PingHeader = new(
        @"PING\s+\S+\s+\((?<ip>\d{1,3}(?:\.\d{1,3}){3})\)", RegexOptions.CultureInvariant);

    private static readonly string[] FailureMarkers =
    [
        "bad address", "unknown host", "name or service not known", "temporary failure in name resolution",
        "could not resolve", "no address associated", "nodename nor servname",
    ];

    public static ResolveCheckResult Parse(string output)
    {
        var match = PingHeader.Match(output);
        if (match.Success && IPAddress.TryParse(match.Groups["ip"].Value, out var address))
        {
            return new ResolveCheckResult(NameResolution.Resolved, address);
        }

        var lower = output.ToLowerInvariant();
        return FailureMarkers.Any(lower.Contains)
            ? new ResolveCheckResult(NameResolution.NotResolved, null)
            : new ResolveCheckResult(NameResolution.Unknown, null);
    }
}

public sealed record HostsEntry(IPAddress? Address, string Line, bool IsMalformed);

/// <summary>Finds lines in /etc/hosts that mention a host name, so entries are never duplicated.</summary>
public static class HostsFile
{
    public static IReadOnlyList<HostsEntry> FindEntriesFor(string content, string host)
    {
        var result = new List<HostsEntry>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (!tokens.Any(t => t.Equals(host, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            IPAddress? address = null;
            var hasAddressFirst = tokens[0].Contains('.') && IPAddress.TryParse(tokens[0], out address);
            var hostAfterAddress = tokens.Skip(1).Any(t => t.Equals(host, StringComparison.OrdinalIgnoreCase));
            result.Add(hasAddressFirst && hostAfterAddress
                ? new HostsEntry(address, raw.Trim(), false)

                // For example the legacy line "unifi.oneacrefund.org unifi", which has no IP address.
                : new HostsEntry(null, raw.Trim(), true));
        }

        return result;
    }
}
