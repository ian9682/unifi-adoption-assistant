using System.Net;
using System.Text.RegularExpressions;

namespace UniFiAdoption.Core.Ssh;

public enum CredentialSource
{
    /// <summary>The publicly documented factory login of a reset UniFi device.</summary>
    FactoryDefault,

    /// <summary>Typed in Admin settings for this session only.</summary>
    ItProvidedThisSession,

    /// <summary>Saved in Admin settings on this computer, encrypted for the Windows user.</summary>
    ItSavedOnThisComputer,
}

/// <summary>A device login. <see cref="ToString"/> never includes the password.</summary>
public sealed record SshCredential(string Username, string Password, CredentialSource Source)
{
    public string SourceDescription => Source switch
    {
        CredentialSource.FactoryDefault => "factory default login",
        CredentialSource.ItProvidedThisSession => "login entered in Admin settings for this session",
        CredentialSource.ItSavedOnThisComputer => "login saved in Admin settings on this computer",
        _ => "unknown login source",
    };

    public override string ToString() => $"user '{Username}' ({SourceDescription})";
}

public enum HostKeyCheck
{
    /// <summary>Matches the key recorded earlier (or pinned by IT) for this MAC address.</summary>
    TrustedKnown,

    /// <summary>No key recorded for this MAC yet; accepted and recorded once identity is confirmed.</summary>
    TrustedFirstUse,

    /// <summary>Differs from the recorded or pinned key. The connection is refused.</summary>
    Rejected,
}

public sealed record SshConnectRequest(
    IPAddress Address,
    int Port,
    SshCredential Credential,
    TimeSpan ConnectTimeout,
    Func<string, string, HostKeyCheck> VerifyHostKey);

public sealed record CommandResult(string Command, int? ExitStatus, string Output, string Error, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitStatus == 0;

    public string Combined => string.IsNullOrEmpty(Error) ? Output : $"{Output}\n{Error}";
}

/// <summary>One step of an interactive session: text to send and the pattern that ends the step.</summary>
public sealed record InteractiveStep(string Send, Regex Expect);

public sealed record InteractiveResult(bool Completed, string Transcript, int StepsCompleted);

public interface ISshSession : IDisposable
{
    string? HostKeyAlgorithm { get; }

    string? HostKeyFingerprint { get; }

    /// <summary>Runs one command in a non-interactive exec channel.</summary>
    Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Drives an interactive shell (pseudo-terminal), for tools that only work interactively.</summary>
    Task<InteractiveResult> RunInteractiveAsync(IReadOnlyList<InteractiveStep> steps, TimeSpan stepTimeout, CancellationToken cancellationToken);
}

public interface ISshConnector
{
    /// <summary>Connects and authenticates.</summary>
    /// <exception cref="SshAuthenticationFailedException">The login was refused.</exception>
    /// <exception cref="SshHostKeyRejectedException">The host key did not match the recorded key.</exception>
    /// <exception cref="SshUnavailableException">The device could not be reached over SSH.</exception>
    Task<ISshSession> ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken);
}

public sealed class SshAuthenticationFailedException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class SshUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class SshHostKeyRejectedException(string? algorithm, string? fingerprint, Exception? inner = null)
    : Exception($"The device presented host key {algorithm} SHA256:{fingerprint}, which does not match the key recorded for it.", inner)
{
    public string? Algorithm { get; } = algorithm;

    public string? Fingerprint { get; } = fingerprint;
}
