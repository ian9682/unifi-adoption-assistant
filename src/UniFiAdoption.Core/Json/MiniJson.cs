using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace UniFiAdoption.Core.Json;

/// <summary>
/// A small JSON reader and writer, so the app needs no extra libraries on .NET Framework.
/// Objects become <see cref="Dictionary{TKey,TValue}"/> (case-insensitive keys), arrays become
/// <see cref="List{T}"/>, numbers become <see cref="double"/>. Comments (// and /* */) and trailing
/// commas are accepted because settings files are edited by hand.
/// </summary>
public static class MiniJson
{
    public static object? Parse(string text)
    {
        var parser = new Parser(text);
        var value = parser.ParseValue();
        parser.SkipWhitespace();
        if (!parser.AtEnd)
        {
            throw parser.Error("Unexpected text after the end of the document");
        }

        return value;
    }

    public static string Serialize(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value, 0);
        return builder.ToString();
    }

    /// <summary>Copies values from a parsed JSON object onto matching writable properties (case-insensitive).</summary>
    public static void Populate(object target, IDictionary<string, object?> json, List<string> problems, List<string> warnings)
    {
        var properties = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in json)
        {
            if (!properties.TryGetValue(pair.Key, out var property))
            {
                warnings.Add($"Unknown setting '{pair.Key}' was ignored.");
                continue;
            }

            if (TryConvert(pair.Value, property.PropertyType, out var converted))
            {
                property.SetValue(target, converted);
            }
            else
            {
                problems.Add($"Setting '{pair.Key}' has a value of the wrong type.");
            }
        }
    }

    private static bool TryConvert(object? value, Type type, out object? result)
    {
        result = null;
        if (type == typeof(string))
        {
            if (value is null or string)
            {
                result = value;
                return true;
            }

            return false;
        }

        if (type == typeof(bool) && value is bool b)
        {
            result = b;
            return true;
        }

        if (type == typeof(int) && value is double d && Math.Abs(d - Math.Round(d)) < 1e-9 && d >= int.MinValue && d <= int.MaxValue)
        {
            result = (int)d;
            return true;
        }

        if (type == typeof(List<string>) && value is List<object?> list && list.All(i => i is string))
        {
            result = list.Cast<string>().ToList();
            return true;
        }

        if (type == typeof(Dictionary<string, string>) && value is Dictionary<string, object?> map && map.Values.All(v => v is string))
        {
            result = map.ToDictionary(p => p.Key, p => (string)p.Value!, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        return false;
    }

    private static void Write(StringBuilder b, object? value, int indent)
    {
        switch (value)
        {
            case null:
                b.Append("null");
                break;
            case string s:
                WriteString(b, s);
                break;
            case bool flag:
                b.Append(flag ? "true" : "false");
                break;
            case int or long or double or float or decimal:
                b.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                WriteString(b, dto.ToString("o", CultureInfo.InvariantCulture));
                break;
            case IDictionary dictionary:
                b.Append('{');
                var first = true;
                foreach (DictionaryEntry entry in dictionary)
                {
                    b.Append(first ? "\n" : ",\n").Append(' ', (indent + 1) * 2);
                    WriteString(b, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                    b.Append(": ");
                    Write(b, entry.Value, indent + 1);
                    first = false;
                }

                b.Append(first ? "}" : "\n" + new string(' ', indent * 2) + "}");
                break;
            case IEnumerable items:
                b.Append('[');
                var any = false;
                foreach (var item in items)
                {
                    b.Append(any ? ", " : string.Empty);
                    Write(b, item, indent + 1);
                    any = true;
                }

                b.Append(']');
                break;
            default:
                WriteString(b, value.ToString() ?? string.Empty);
                break;
        }
    }

    private static void WriteString(StringBuilder b, string s)
    {
        b.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        b.Append(c);
                    }

                    break;
            }
        }

        b.Append('"');
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        public FormatException Error(string message) => new($"{message} at position {_pos}.");

        public void SkipWhitespace()
        {
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (char.IsWhiteSpace(c))
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/')
                {
                    while (_pos < text.Length && text[_pos] != '\n')
                    {
                        _pos++;
                    }
                }
                else if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '*')
                {
                    var end = text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw Error("Unclosed comment");
                    }

                    _pos = end + 2;
                }
                else
                {
                    break;
                }
            }
        }

        public object? ParseValue()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                throw Error("Unexpected end of document");
            }

            var c = text[_pos];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return ParseString();
                case 't': return Literal("true", true);
                case 'f': return Literal("false", false);
                case 'n': return Literal("null", null);
                default:
                    if (c == '-' || char.IsDigit(c))
                    {
                        return ParseNumber();
                    }

                    throw Error($"Unexpected character '{c}'");
            }
        }

        private object? Literal(string word, object? value)
        {
            if (string.CompareOrdinal(text, _pos, word, 0, word.Length) != 0)
            {
                throw Error("Unexpected value");
            }

            _pos += word.Length;
            return value;
        }

        private Dictionary<string, object?> ParseObject()
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _pos++; // {
            while (true)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("Unclosed object");
                }

                if (text[_pos] == '}')
                {
                    _pos++;
                    return result;
                }

                if (text[_pos] != '"')
                {
                    throw Error("Expected a property name");
                }

                var key = ParseString();
                SkipWhitespace();
                if (AtEnd || text[_pos] != ':')
                {
                    throw Error("Expected ':'");
                }

                _pos++;
                result[key] = ParseValue();
                SkipWhitespace();
                if (!AtEnd && text[_pos] == ',')
                {
                    _pos++;
                }
                else if (AtEnd || text[_pos] != '}')
                {
                    throw Error("Expected ',' or '}'");
                }
            }
        }

        private List<object?> ParseArray()
        {
            var result = new List<object?>();
            _pos++; // [
            while (true)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("Unclosed array");
                }

                if (text[_pos] == ']')
                {
                    _pos++;
                    return result;
                }

                result.Add(ParseValue());
                SkipWhitespace();
                if (!AtEnd && text[_pos] == ',')
                {
                    _pos++;
                }
                else if (AtEnd || text[_pos] != ']')
                {
                    throw Error("Expected ',' or ']'");
                }
            }
        }

        private string ParseString()
        {
            var b = new StringBuilder();
            _pos++; // opening quote
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("Unclosed string");
                }

                var c = text[_pos++];
                if (c == '"')
                {
                    return b.ToString();
                }

                if (c != '\\')
                {
                    b.Append(c);
                    continue;
                }

                if (AtEnd)
                {
                    throw Error("Unclosed string");
                }

                var e = text[_pos++];
                switch (e)
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (_pos + 4 > text.Length ||
                            !int.TryParse(text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw Error("Invalid \\u escape");
                        }

                        b.Append((char)code);
                        _pos += 4;
                        break;
                    default:
                        throw Error($"Invalid escape '\\{e}'");
                }
            }
        }

        private double ParseNumber()
        {
            var start = _pos;
            while (!AtEnd && "+-0123456789.eE".IndexOf(text[_pos]) >= 0)
            {
                _pos++;
            }

            if (!double.TryParse(text.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                throw Error("Invalid number");
            }

            return number;
        }
    }
}
