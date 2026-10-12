using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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

    /// <summary>How the program refers to support in messages, e.g. "Contact your IT team".</summary>
    public string SupportContact { get; set; } = "your IT team";

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
    public List<string> ExtraUbiquitiOuis { get; set; } = [];

    /// <summary>The device's reported model must start with one of these (the AC Pro reports UAP-AC-Pro...).</summary>
    public List<string> SupportedModelPrefixes { get; set; } = ["UAP"];

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
    public static IReadOnlyList<string> BuiltInUbiquitiOuis { get; } =
    [
        "00156D", "002722", "0418D6", "0CEA14", "18E829", "1C6A1B", "245A4C", "24A43C", "28704E",
        "44D9E7", "602232", "687251", "68D79A", "6C63F8", "70A741", "7483C2", "74ACB9", "784558",
        "788A20", "802AA8", "847848", "942A6F", "9C05D6", "AC8BA9", "B4FBE4", "D021F9", "D8B370",
        "DC9FDB", "E063DA", "E43883", "F09FC2", "F492BF", "FCECDA",
    ];

    public IReadOnlyCollection<string> AllUbiquitiOuis =>
        BuiltInUbiquitiOuis
            .Concat(ExtraUbiquitiOuis.Select(o => new string(o.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant()))
            .Where(o => o.Length == 6)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps numbers within sensible bounds instead of failing on a typo.</summary>
    public void Normalize()
    {
        SshPort = Math.Clamp(SshPort, 1, 65535);
        DiscoveryListenSeconds = Math.Clamp(DiscoveryListenSeconds, 1, 30);
        MaxHostsPerInterface = Math.Clamp(MaxHostsPerInterface, 16, 4094);
        ArpParallelism = Math.Clamp(ArpParallelism, 1, 256);
        SshConnectTimeoutSeconds = Math.Clamp(SshConnectTimeoutSeconds, 3, 120);
        SshCommandTimeoutSeconds = Math.Clamp(SshCommandTimeoutSeconds, 3, 300);
        VerifySeconds = Math.Clamp(VerifySeconds, 0, 600);
        VerifyPollSeconds = Math.Clamp(VerifyPollSeconds, 1, 60);
        SupportContact = string.IsNullOrWhiteSpace(SupportContact) ? "your IT team" : SupportContact.Trim();
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

public sealed record SettingsLoadResult(AssistantSettings Settings, string? SourcePath, IReadOnlyList<string> Problems);

/// <summary>
/// Looks for settings.json next to the program first (so IT can ship a pre-configured copy), then in
/// %ProgramData%\UniFiAdoptionAssistant\ (for centrally managed computers). The first file found wins.
/// </summary>
public static class SettingsLoader
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<string> DefaultCandidatePaths() =>
    [
        Path.Combine(AppContext.BaseDirectory, FileName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniFiAdoptionAssistant", FileName),
    ];

    public static SettingsLoadResult Load(IEnumerable<string>? candidatePaths = null)
    {
        foreach (var path in candidatePaths ?? DefaultCandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var settings = JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(path), JsonOptions) ?? new AssistantSettings();
                settings.Normalize();
                return new SettingsLoadResult(settings, path, settings.Validate());
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                var fallback = new AssistantSettings();
                fallback.Normalize();
                return new SettingsLoadResult(fallback, path, [$"{path} could not be read: {ex.Message}"]);
            }
        }

        var defaults = new AssistantSettings();
        defaults.Normalize();
        return new SettingsLoadResult(defaults, null, defaults.Validate());
    }
}
