using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KiComLibBackend.Services.Reviews;

/// <summary>RFC 8785: ordinal UTF-16 keys, ECMAScript binary64 numbers, UTF-8.</summary>
public static class ReviewJson
{
    public static string Canonical(JsonNode? value)
    {
        StringBuilder result = new();
        Write(value, result);
        return result.ToString();
    }

    public static string Digest(string canonical) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private static void Write(JsonNode? node, StringBuilder result)
    {
        if (node == null) { result.Append("null"); return; }
        if (node is JsonObject obj)
        {
            result.Append('{');
            bool first = true;
            foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!first) result.Append(',');
                first = false;
                String(pair.Key, result); result.Append(':'); Write(pair.Value, result);
            }
            result.Append('}');
        }
        else if (node is JsonArray array)
        {
            result.Append('[');
            for (int i = 0; i < array.Count; i++) { if (i > 0) result.Append(','); Write(array[i], result); }
            result.Append(']');
        }
        else if (node.GetValueKind() == JsonValueKind.String) String(node.GetValue<string>(), result);
        else
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            var value = document.RootElement;
            switch (value.ValueKind)
            {
                case JsonValueKind.String: String(value.GetString()!, result); break;
                case JsonValueKind.Number: result.Append(Number(value.GetDouble())); break;
                case JsonValueKind.True: result.Append("true"); break;
                case JsonValueKind.False: result.Append("false"); break;
                default: throw new FormatException("Unsupported JSON value");
            }
        }
    }

    private static void String(string value, StringBuilder result)
    {
        result.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) throw new FormatException("Unpaired surrogate");
                result.Append(c).Append(value[++i]); continue;
            }
            if (char.IsLowSurrogate(c)) throw new FormatException("Unpaired surrogate");
            result.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString()
            });
        }
        result.Append('"');
    }

    internal static string Number(double value)
    {
        if (!double.IsFinite(value)) throw new FormatException("Non-finite number");
        if (value == 0) return "0";
        string sign = value < 0 ? "-" : "";
        string raw = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        string[] pieces = raw.Split('e');
        int exponent = pieces.Length == 2 ? int.Parse(pieces[1], CultureInfo.InvariantCulture) : 0;
        int dot = pieces[0].IndexOf('.');
        int point = (dot < 0 ? pieces[0].Length : dot) + exponent;
        string digits = pieces[0].Replace(".", "");
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; point--; }
        digits = digits.TrimEnd('0');
        if (point > 0 && point <= 21)
            return sign + (point >= digits.Length ? digits.PadRight(point, '0') : digits.Insert(point, "."));
        if (point <= 0 && point > -6)
            return sign + "0." + new string('0', -point) + digits;
        int power = point - 1;
        return sign + digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (power >= 0 ? "+" : "") + power.ToString(CultureInfo.InvariantCulture);
    }
}
