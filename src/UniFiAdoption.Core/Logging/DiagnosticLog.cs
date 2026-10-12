using System.Globalization;

namespace UniFiAdoption.Core.Logging;

/// <summary>
/// Plain-text diagnostic log for IT, written to %LOCALAPPDATA%\UniFiAdoptionAssistant\logs.
/// Passwords are never passed to the log; as a second safeguard, any registered secret is masked
/// before a line is stored or written.
/// </summary>
public sealed class DiagnosticLog
{
    private const int MaxLineLength = 4000;
    private readonly object _gate = new();
    private readonly List<string> _lines = [];
    private readonly List<string> _secrets = [];

    public DiagnosticLog(string? filePath)
    {
        FilePath = filePath;
        if (filePath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FilePath = null; // keep logging in memory only
            }
        }
    }

    public string? FilePath { get; private set; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniFiAdoptionAssistant", "logs");

    /// <summary>Creates today's log file and removes log files older than 30 days.</summary>
    public static DiagnosticLog CreateDefault()
    {
        var directory = DefaultDirectory;
        try
        {
            if (Directory.Exists(directory))
            {
                foreach (var old in Directory.EnumerateFiles(directory, "*.log")
                             .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)))
                {
                    File.Delete(old);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort.
        }

        return new DiagnosticLog(Path.Combine(directory, $"assistant-{DateTime.Now:yyyyMMdd}.log"));
    }

    public void AddSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
        {
            lock (_gate)
            {
                if (!_secrets.Contains(secret))
                {
                    _secrets.Add(secret);
                }
            }
        }
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");

    /// <summary>Lines logged so far by this instance (already redacted).</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToList();
        }
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}";
        lock (_gate)
        {
            foreach (var secret in _secrets)
            {
                line = line.Replace(secret, "********", StringComparison.Ordinal);
            }

            if (line.Length > MaxLineLength)
            {
                line = line[..MaxLineLength] + " …(truncated)";
            }

            _lines.Add(line);
            if (FilePath is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never break the setup.
            }
        }
    }
}
