using System.Globalization;
using System.Text.Json;

namespace AIUsage.Core.Support;

/// <summary>
/// Shared, behavior-free parsing helpers used by more than one provider — the C# counterpart of the
/// Swift edition's ProviderParse.
/// </summary>
public static class ProviderParse
{
    /// <summary>Parse a JSON object from raw response bytes. Empty body -&gt; null. Malformed body is logged and returns null.</summary>
    public static JsonElement? JsonObject(byte[] data)
    {
        if (data.Length == 0) return null;
        try
        {
            var doc = JsonDocument.Parse(data);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Http, $"response body is not valid JSON ({data.Length} bytes): {ex.Message}");
            return null;
        }
    }

    public static double? Number(JsonElement? element)
    {
        if (element is not { } e) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
            case JsonValueKind.String:
                if (double.TryParse(e.GetString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s))
                    return s;
                return null;
            default:
                return null;
        }
    }

    public static double? Number(object? value)
    {
        return value switch
        {
            double d when double.IsFinite(d) => d,
            float f when float.IsFinite(f) => f,
            int i => i,
            long l => l,
            string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && double.IsFinite(r) => r,
            _ => null
        };
    }

    public static bool? Bool(JsonElement? element)
    {
        if (element is not { } e) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return e.TryGetDouble(out var d) && d != 0;
            case JsonValueKind.String:
                var s = e.GetString()?.Trim().ToLowerInvariant();
                return s switch { "true" or "1" => true, "false" or "0" => false, _ => (bool?)null };
            default: return null;
        }
    }

    /// <summary>Clamp a percentage into 0...100, treating non-finite input as 0.</summary>
    public static double ClampPercent(double value)
    {
        if (!double.IsFinite(value)) return 0;
        return Math.Min(Math.Max(value, 0), 100);
    }

    /// <summary>Convert integer cents to dollars, snapping to whole cents first to guard against float drift.</summary>
    public static double CentsToDollars(double cents) => Math.Round(cents, MidpointRounding.AwayFromZero) / 100.0;

    /// <summary>Decode a JWT's payload (the middle segment) as a JSON object.</summary>
    public static JsonElement? JwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        while (payload.Length % 4 != 0) payload += "=";
        try
        {
            var bytes = Convert.FromBase64String(payload);
            var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Unwrap a `go-keyring-base64:`-prefixed value (how Go tools store secrets in a keyring/credential store).</summary>
    public static string? UnwrapGoKeyring(string raw)
    {
        var text = raw.Trim();
        const string prefix = "go-keyring-base64:";
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            var encoded = text[prefix.Length..].Trim();
            try
            {
                var bytes = Convert.FromBase64String(encoded);
                text = System.Text.Encoding.UTF8.GetString(bytes).Trim();
            }
            catch
            {
                return null;
            }
        }
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Decode JSON text into T, falling back to hex-decoding the text first (some credential files are hex-encoded JSON).</summary>
    public static T? DecodeJsonWithHexFallback<T>(string text)
    {
        var direct = TryDecodeJson<T>(text);
        if (direct is not null) return direct;

        var hex = text.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit)) return default;

        try
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            return TryDecodeJson<T>(decoded);
        }
        catch
        {
            return default;
        }
    }

    private static T? TryDecodeJson<T>(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonDefaults.Options);
        }
        catch
        {
            return default;
        }
    }
}

public static class JsonDefaults
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public static class StringExtensions
{
    /// <summary>Percent-encode one application/x-www-form-urlencoded value (RFC 3986 unreserved characters pass through).</summary>
    public static string UrlFormEncoded(this string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            if ((b >= 0x41 && b <= 0x5A) || (b >= 0x61 && b <= 0x7A) || (b >= 0x30 && b <= 0x39)
                || b == 0x2D || b == 0x2E || b == 0x5F || b == 0x7E)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }
        return sb.ToString();
    }

    public static string? NilIfEmpty(this string? value) => string.IsNullOrEmpty(value) ? null : value;

    public static string TrimmingTrailingSlashes(this string value)
    {
        var result = value;
        while (result.EndsWith('/')) result = result[..^1];
        return result;
    }

    /// <summary>Title-case a plan name: split on separator, upper-case each word's first character.</summary>
    public static string TitleCased(this string value, Func<char, bool> isSeparator, bool lowercasingTail = false)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (isSeparator(c))
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) words.Add(current.ToString());

        return string.Join(" ", words.Select(w =>
        {
            var head = w[..1].ToUpperInvariant();
            var tail = lowercasingTail ? w[1..].ToLowerInvariant() : w[1..];
            return head + tail;
        }));
    }
}
