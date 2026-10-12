using System.Text.Json;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Ssh;

namespace UniFiAdoption.Core.Security;

public interface IHostKeyStore
{
    HostKeyCheck Check(MacAddress mac, string algorithm, string fingerprint);

    void Remember(MacAddress mac, string algorithm, string fingerprint);

    void Clear();
}

/// <summary>
/// SSH host keys recorded per device MAC address (IP addresses change with DHCP, MACs do not).
///
/// Policy: a key pinned by IT in settings must match exactly. Otherwise the first key seen for a MAC
/// is accepted ("trust on first use") and recorded only after the device has proved, once logged
/// in, that it has the expected MAC. Any later change is refused until IT clears the record,
/// which is expected after a factory reset because the device generates new keys.
/// </summary>
public sealed class JsonHostKeyStore : IHostKeyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly IReadOnlyDictionary<string, string> _pinned;
    private Dictionary<string, Dictionary<string, KnownKey>> _known;

    public JsonHostKeyStore(string path, IReadOnlyDictionary<string, string>? pinnedFingerprints = null)
    {
        _path = path;
        _pinned = (pinnedFingerprints ?? new Dictionary<string, string>())
            .Where(p => MacAddress.TryParse(p.Key, out _))
            .ToDictionary(p => MacAddress.Parse(p.Key).Hex, p => Normalize(p.Value));
        _known = Load(path);
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniFiAdoptionAssistant", "known-devices.json");

    public HostKeyCheck Check(MacAddress mac, string algorithm, string fingerprint)
    {
        var fp = Normalize(fingerprint);
        if (_pinned.TryGetValue(mac.Hex, out var pinned))
        {
            return string.Equals(pinned, fp, StringComparison.Ordinal) ? HostKeyCheck.TrustedKnown : HostKeyCheck.Rejected;
        }

        lock (_gate)
        {
            if (_known.TryGetValue(mac.Hex, out var keys) && keys.TryGetValue(algorithm, out var known))
            {
                return string.Equals(known.Fingerprint, fp, StringComparison.Ordinal) ? HostKeyCheck.TrustedKnown : HostKeyCheck.Rejected;
            }
        }

        return HostKeyCheck.TrustedFirstUse;
    }

    public void Remember(MacAddress mac, string algorithm, string fingerprint)
    {
        lock (_gate)
        {
            if (!_known.TryGetValue(mac.Hex, out var keys))
            {
                keys = new Dictionary<string, KnownKey>(StringComparer.Ordinal);
                _known[mac.Hex] = keys;
            }

            if (keys.ContainsKey(algorithm))
            {
                return;
            }

            keys[algorithm] = new KnownKey(Normalize(fingerprint), DateTimeOffset.UtcNow);
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _known = new Dictionary<string, Dictionary<string, KnownKey>>(StringComparer.Ordinal);
            Save();
        }
    }

    private static string Normalize(string fingerprint)
    {
        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..];
        }

        return value.TrimEnd('=');
    }

    private static Dictionary<string, Dictionary<string, KnownKey>> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, KnownKey>>>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    return new Dictionary<string, Dictionary<string, KnownKey>>(loaded, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file is treated as empty; it will be rewritten on the next save.
        }

        return new Dictionary<string, Dictionary<string, KnownKey>>(StringComparer.Ordinal);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_known, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to record a key only weakens later checks; it must not stop the setup.
        }
    }

    public sealed record KnownKey(string Fingerprint, DateTimeOffset FirstSeenUtc);
}
