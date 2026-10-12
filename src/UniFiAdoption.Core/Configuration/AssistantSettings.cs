using System.Net;
using System.Net.Sockets;
using UniFiAdoption.Core.Json;
using UniFiAdoption.Core.Device;
using UniFiAdoption.Core.Networking;

namespace UniFiAdoption.Core.Configuration;

/// <summary>
/// Administrator settings. Every value has a working default, so the program runs without a
/// settings file. IT can override values with a settings.json file (see SettingsLoader).
/// No secrets belong here: the only built-in login is the publicly documented factory default.
/// </summary>
public sealed class AssistantSettings
{
    /// <summary>The inform URL given to the access point.</summary>
    public string InformUrl { get; set; } = "http://unifi.oneacrefund.org:8080/inform";

    /// <summary>How the program refers to support in messages, e.g. "Contact the ITO Networks Team".</summary>
    public string SupportContact { get; set; } = "the ITO Networks Team";

    /// <summary>Try the factory default login (ubnt/ubnt) on the verified device.</summary>
    public bool AllowFactoryDefaultCredentials { get; set; } = true;

    public string FactoryDefaultUsername { get; set; } = "ubnt";

    public string FactoryDefaultPassword { get; set; } = "ubnt";

    /// <summary>
    /// Optional. If the access point cannot resolve the controller name, add
    /// "&lt;this IP&gt; &lt;controller host&gt;" to its /etc/hosts. Leave empty to never touch /etc/hosts.
    /// </summary>
    public string? HostsFallbackControllerIp { get; set; }

    /// <summary>Only connect to devices that answered Ubiquiti discovery or have a Ubiquiti OUI.</summary>
    public bool RequireUbiquitiIdentity { get; set; } = true;

    /// <summary>Additional Ubiquiti OUIs (first 6 hex digits) to accept, added to the built-in list.</summary>
    public List<string> ExtraUbiquitiOuis { get; set; } = new();

    /// <summary>The device's reported model must start with one of these (the AC Pro reports UAP-AC-Pro...).</summary>
    public List<string> SupportedModelPrefixes { get; set; } = new() { "UAP" };

    /// <summary>Use the legacy interactive mca-cli session if mca-cli-op is missing.</summary>
    public bool AllowInteractiveMcaCliFallback { get; set; } = true;

    public int SshPort { get; set; } = 22;

    public int DiscoveryListenSeconds { get; set; } = 4;

    public int MaxHostsPerInterface { get; set; } = 1024;

    public int ArpParallelism { get; set; } = 64;

    public int SshConnectTimeoutSeconds { get; set; } = 12;

    public int SshCommandTimeoutSeconds { get; set; } = 20;

    /// <summary>How long to watch the device's status after set-inform.</summary>
    public int VerifySeconds { get; set; } = 45;

    public int VerifyPollSeconds { get; set; } = 5;

    /// <summary>How many access points a batch sets up at the same time.</summary>
    public int BatchConcurrency { get; set; } = 3;

    /// <summary>How long a batch watches each access point after sending the address (the live list keeps checking after that).</summary>
    public int BatchVerifySeconds { get; set; } = 15;

    /// <summary>How often the live list repeats the discovery broadcast.</summary>
    public int DiscoveryIntervalSeconds { get; set; } = 30;

    /// <summary>How often the live list repeats the full ARP sweep.</summary>
    public int ArpSweepIntervalSeconds { get; set; } = 120;

    /// <summary>How often an access point waiting for approval is checked to see whether it has been adopted.</summary>
    public int RecheckWaitingSeconds { get; set; } = 45;

    /// <summary>An access point not seen for this long is shown as "Not responding".</summary>
    public int UnreachableAfterSeconds { get; set; } = 300;

    /// <summary>How many access points the live list checks at the same time.</summary>
    public int CheckConcurrency { get; set; } = 4;

    /// <summary>Optional: MAC address → expected SHA256 host-key fingerprint (from the log of a trusted run).</summary>
    public Dictionary<string, string> PinnedHostKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Start in test mode: discover and check, but never change the device.</summary>
    public bool TestMode { get; set; }

    /// <summary>Optional photo (PNG/JPG) to show instead of the built-in drawing, e.g. of your own device's label.</summary>
    public string? DeviceImagePath { get; set; }

    /// <summary>
    /// Ubiquiti OUIs commonly seen on UniFi access points. Used only when a device did not answer the
    /// Ubiquiti discovery broadcast. IT should confirm against the IEEE registry and use
    /// ExtraUbiquitiOuis for any missing prefix.
    /// </summary>
    public static IReadOnlyList<string> BuiltInUbiquitiOuis { get; } = new[]
    {
        "00156D", "002722", "0418D6", "0CEA14", "18E829", "1C6A1B", "245A4C", "24A43C", "28704E",
        "44D9E7", "602232", "687251", "68D79A", "6C63F8", "70A741", "7483C2", "74ACB9", "784558",
        "788A20", "802AA8", "847848", "942A6F", "9C05D6", "AC8BA9", "B4FBE4", "D021F9", "D8B370",
        "DC9FDB", "E063DA", "E43883", "F09FC2", "F492BF", "FCECDA",
    };

    public IReadOnlyCollection<string> AllUbiquitiOuis =>
        BuiltInUbiquitiOuis
            .Concat(ExtraUbiquitiOuis.Select(o => new string(o.Where(Compat.IsHex).ToArray()).ToUpperInvariant()))
            .Where(o => o.Length == 6)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps numbers within sensible bounds instead of failing on a typo.</summary>
    public void Normalize()
    {
        SshPort = Compat.Clamp(SshPort, 1, 65535);
        DiscoveryListenSeconds = Compat.Clamp(DiscoveryListenSeconds, 1, 30);
        MaxHostsPerInterface = Compat.Clamp(MaxHostsPerInterface, 16, 4094);
        ArpParallelism = Compat.Clamp(ArpParallelism, 1, 256);
        SshConnectTimeoutSeconds = Compat.Clamp(SshConnectTimeoutSeconds, 3, 120);
        SshCommandTimeoutSeconds = Compat.Clamp(SshCommandTimeoutSeconds, 3, 300);
        VerifySeconds = Compat.Clamp(VerifySeconds, 0, 600);
        VerifyPollSeconds = Compat.Clamp(VerifyPollSeconds, 1, 60);
        BatchConcurrency = Compat.Clamp(BatchConcurrency, 1, 8);
        BatchVerifySeconds = Compat.Clamp(BatchVerifySeconds, 0, 300);
        DiscoveryIntervalSeconds = Compat.Clamp(DiscoveryIntervalSeconds, 5, 600);
        ArpSweepIntervalSeconds = Compat.Clamp(ArpSweepIntervalSeconds, 30, 3600);
        RecheckWaitingSeconds = Compat.Clamp(RecheckWaitingSeconds, 10, 3600);
        UnreachableAfterSeconds = Compat.Clamp(UnreachableAfterSeconds, 60, 3600);
        CheckConcurrency = Compat.Clamp(CheckConcurrency, 1, 8);
        SupportContact = string.IsNullOrWhiteSpace(SupportContact) ? "the ITO Networks Team" : SupportContact.Trim();
        SupportedModelPrefixes = SupportedModelPrefixes.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
    }

    /// <summary>Problems that make the settings unusable. An empty list means the settings are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!ControllerTarget.TryCreate(InformUrl, out _, out var urlProblem))
        {
            problems.Add(urlProblem!);
        }

        if (!string.IsNullOrWhiteSpace(HostsFallbackControllerIp) &&
            !(IPAddress.TryParse(HostsFallbackControllerIp, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork))
        {
            problems.Add("HostsFallbackControllerIp must be an IPv4 address or empty.");
        }

        if (AllowFactoryDefaultCredentials && (string.IsNullOrEmpty(FactoryDefaultUsername) || string.IsNullOrEmpty(FactoryDefaultPassword)))
        {
            problems.Add("FactoryDefaultUsername and FactoryDefaultPassword must not be empty when factory logins are allowed.");
        }

        foreach (var key in PinnedHostKeys.Keys.Where(k => !MacAddress.TryParse(k, out _)))
        {
            problems.Add($"PinnedHostKeys contains '{key}', which is not a MAC address.");
        }

        if (SupportedModelPrefixes.Count == 0)
        {
            problems.Add("SupportedModelPrefixes must contain at least one prefix.");
        }

        return problems;
    }
}

public sealed record SettingsLoadResult(AssistantSettings Settings, string? SourcePath, IReadOnlyList<string> Problems)
{
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Looks for settings.json next to the program first (so IT can ship a pre-configured copy), then in
/// %ProgramData%\UniFiAdoptionAssistant\ (for centrally managed computers). The first file found wins.
/// </summary>
public static class SettingsLoader
{
    public const string FileName = "settings.json";

    public static IReadOnlyList<string> DefaultCandidatePaths() => new[]
    {
        Path.Combine(AppContext.BaseDirectory, FileName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniFiAdoptionAssistant", FileName),
    };

    public static SettingsLoadResult Load(IEnumerable<string>? candidatePaths = null)
    {
        foreach (var path in candidatePaths ?? DefaultCandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var settings = new AssistantSettings();
            var problems = new List<string>();
            var warnings = new List<string>();
            try
            {
                if (MiniJson.Parse(File.ReadAllText(path)) is Dictionary<string, object?> json)
                {
                    MiniJson.Populate(settings, json, problems, warnings);
                }
                else
                {
                    problems.Add($"{path} does not contain a JSON object.");
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                settings = new AssistantSettings();
                problems.Add($"{path} could not be read: {ex.Message}");
            }

            settings.Normalize();
            problems.AddRange(settings.Validate());
            return new SettingsLoadResult(settings, path, problems) { Warnings = warnings };
        }

        var defaults = new AssistantSettings();
        defaults.Normalize();
        return new SettingsLoadResult(defaults, null, defaults.Validate());
    }
}
