using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UniFiAdoption.Core.Json;
using UniFiAdoption.Core.Ssh;

namespace UniFiAdoption.Core.Security;

/// <summary>Optional device login saved by IT on one computer.</summary>
public interface ICredentialStore
{
    bool HasSavedCredential { get; }

    string? SavedUsername { get; }

    SshCredential? LoadSaved();

    void Save(string username, string password);

    void Clear();
}

/// <summary>
/// Stores a device login encrypted with Windows DPAPI for the current Windows user account, so it
/// cannot be read by other accounts or on another computer. Nothing is compiled into the program.
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UniFiAdoptionAssistant/device-login/v1");
    private readonly string _path;

    public DpapiCredentialStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniFiAdoptionAssistant", "device-login.bin");
    }

    public bool HasSavedCredential => File.Exists(_path);

    public string? SavedUsername => LoadSaved()?.Username;

    public SshCredential? LoadSaved()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[]? clear = null;
        try
        {
            clear = Dpapi.Unprotect(File.ReadAllBytes(_path), Entropy);
            return MiniJson.Parse(Encoding.UTF8.GetString(clear)) is Dictionary<string, object?> stored &&
                   stored.TryGetValue("u", out var u) && u is string username && username.Length > 0 &&
                   stored.TryGetValue("p", out var p) && p is string password
                ? new SshCredential(username, password, CredentialSource.ItSavedOnThisComputer)
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (clear is not null)
            {
                Array.Clear(clear, 0, clear.Length);
            }
        }
    }

    public void Save(string username, string password)
    {
        var clear = Encoding.UTF8.GetBytes(MiniJson.Serialize(new Dictionary<string, object?> { ["u"] = username, ["p"] = password }));
        try
        {
            var protectedBytes = Dpapi.Protect(clear, Entropy);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path, protectedBytes);
        }
        finally
        {
            Array.Clear(clear, 0, clear.Length);
        }
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

/// <summary>Minimal DPAPI (CryptProtectData) wrapper for the current user, without extra packages.</summary>
internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: true);

    public static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: false);

    private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
    {
        var input = ToBlob(data);
        var entropyBlob = ToBlob(entropy);
        var output = default(DataBlob);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropyBlob);
            if (output.Data != IntPtr.Zero)
            {
                ZeroUnmanaged(output.Data, output.Size);
                LocalFree(output.Data);
            }
        }
    }

    private static DataBlob ToBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            ZeroUnmanaged(blob.Data, blob.Size);
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private static void ZeroUnmanaged(IntPtr pointer, int length)
    {
        if (length > 0)
        {
            Marshal.Copy(new byte[length], 0, pointer, length);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
