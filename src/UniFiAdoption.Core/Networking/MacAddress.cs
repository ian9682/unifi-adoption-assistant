using System.Globalization;

namespace UniFiAdoption.Core.Networking;

/// <summary>A 48-bit IEEE 802 MAC address.</summary>
public readonly struct MacAddress : IEquatable<MacAddress>
{
    private const ulong ValueMask = 0xFFFF_FFFF_FFFFUL;
    private readonly ulong _value;

    private MacAddress(ulong value) => _value = value & ValueMask;

    /// <summary>Twelve upper-case hex digits without separators, for example <c>802AA812ABCD</c>.</summary>
    public string Hex => _value.ToString("X12", CultureInfo.InvariantCulture);

    /// <summary>The organisationally unique identifier (first six hex digits).</summary>
    public string Oui => Hex.Substring(0, 6);

    public bool IsZero => _value == 0;

    /// <summary>True for multicast and broadcast addresses (the I/G bit is set).</summary>
    public bool IsMulticast => ((_value >> 40) & 0x01) != 0;

    public bool IsUsableUnicast => !IsZero && !IsMulticast;

    public static MacAddress FromBytes(byte[] bytes, int offset = 0)
    {
        if (bytes.Length - offset < 6)
        {
            throw new ArgumentException("A MAC address has exactly 6 bytes.", nameof(bytes));
        }

        ulong value = 0;
        for (var i = offset; i < offset + 6; i++)
        {
            value = (value << 8) | bytes[i];
        }

        return new MacAddress(value);
    }

    /// <summary>Parses 12 hex digits, optionally separated by ':', '-' or '.'.</summary>
    public static bool TryParse(string? text, out MacAddress mac)
    {
        mac = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = new System.Text.StringBuilder(12);
        foreach (var c in text!.Trim())
        {
            if (c is ':' or '-' or '.')
            {
                continue;
            }

            if (!Compat.IsHex(c) || hex.Length == 12)
            {
                return false;
            }

            hex.Append(c);
        }

        if (hex.Length != 12)
        {
            return false;
        }

        mac = new MacAddress(ulong.Parse(hex.ToString(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        return true;
    }

    public static MacAddress Parse(string text) =>
        TryParse(text, out var mac) ? mac : throw new FormatException($"'{text}' is not a MAC address.");

    public bool EndsWith(MacSuffix suffix) => Hex.EndsWith(suffix.Value, StringComparison.Ordinal);

    /// <summary>True if the hex digits typed so far (2 to 12, separators ignored) match the end of this address.</summary>
    public bool EndsWithHex(string? typed)
    {
        var cleaned = MacSuffix.Clean(typed);
        return cleaned.Length >= 2 && Hex.EndsWith(cleaned, StringComparison.Ordinal);
    }

    public override string ToString()
    {
        var hex = Hex;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    public bool Equals(MacAddress other) => _value == other._value;

    public override bool Equals(object? obj) => obj is MacAddress other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(MacAddress left, MacAddress right) => left.Equals(right);

    public static bool operator !=(MacAddress left, MacAddress right) => !left.Equals(right);
}

/// <summary>The last four hex characters of a MAC address, as typed by the person at the site.</summary>
public sealed class MacSuffix
{
    public const int RequiredLength = 4;

    private MacSuffix(string value) => Value = value;

    /// <summary>Four upper-case hex digits.</summary>
    public string Value { get; }

    public override string ToString() => Value;

    /// <summary>
    /// Validates user input. Spaces, ':' and '-' are ignored so "AB:CD" is accepted.
    /// Problems are described in plain language for the person at the site.
    /// </summary>
    /// <summary>Upper-cases and removes spaces, ':' and '-' (so "ab:cd" and "AB CD" both become "ABCD").</summary>
    public static string Clean(string? input) =>
        new string((input ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray()).ToUpperInvariant();

    public static MacSuffixValidation Validate(string? input)
    {
        var cleaned = Clean(input);

        const string Allowed = "A MAC address only uses the numbers 0 to 9 and the letters A to F.";

        if (cleaned.Length == 0)
        {
            return MacSuffixValidation.Invalid("Type the last 4 characters of the MAC address.");
        }

        if (cleaned.IndexOf('O') >= 0)
        {
            return MacSuffixValidation.Invalid($"{Allowed} If you see the letter O, type the number 0 (zero).");
        }

        if (cleaned.IndexOfAny(new[] { 'I', 'L' }) >= 0)
        {
            return MacSuffixValidation.Invalid($"{Allowed} If you see the letter I or L, type the number 1 (one).");
        }

        if (!cleaned.All(Compat.IsHex))
        {
            return MacSuffixValidation.Invalid($"{Allowed} Check the label and try again.");
        }

        if (cleaned.Length != RequiredLength)
        {
            return MacSuffixValidation.Invalid($"Enter exactly 4 characters. You entered {cleaned.Length}.");
        }

        return MacSuffixValidation.Valid(new MacSuffix(cleaned));
    }
}

public sealed record MacSuffixValidation(MacSuffix? Suffix, string? Problem)
{
    public bool IsValid => Suffix is not null;

    internal static MacSuffixValidation Valid(MacSuffix suffix) => new(suffix, null);

    internal static MacSuffixValidation Invalid(string problem) => new(null, problem);
}
