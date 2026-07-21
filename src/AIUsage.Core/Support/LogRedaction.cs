using System.Text;
using System.Text.RegularExpressions;

namespace AIUsage.Core.Support;

/// <summary>
/// Faithful port of the Swift/Rust log redaction helpers. Pure, no I/O. `RedactLogMessage` is the
/// lightweight last line of defense every AppLog line passes through; URL/body-specific redaction
/// (RedactUrl / BodyPreview) must be applied explicitly by callers that log a URL or response body.
/// </summary>
public static class LogRedaction
{
    public static string RedactValue(string value)
    {
        if (value.Length <= 12) return "[REDACTED]";
        return $"{value[..4]}...{value[^4..]}";
    }

    private static readonly string[] UrlSensitiveParams =
    {
        "key", "api_key", "apikey", "token", "access_token", "secret", "password",
        "auth", "authorization", "bearer", "credential", "user", "user_id", "userid",
        "account_id", "accountid", "profilearn", "profile_arn", "email", "login"
    };

    public static string RedactUrl(string url)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0) return url;
        var basePart = url[..(queryStart + 1)];
        var query = url[(queryStart + 1)..];

        var parts = query.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq < 0) continue;
            var name = parts[i][..eq];
            var value = parts[i][(eq + 1)..];
            var nameLower = name.ToLowerInvariant();
            if (value.Length > 0 && UrlSensitiveParams.Any(p => nameLower.Contains(p)))
            {
                parts[i] = $"{name}={RedactValue(value)}";
            }
        }
        return basePart + string.Join("&", parts);
    }

    private static readonly string[] JsonSensitiveKeys =
    {
        "name", "password", "token", "access_token", "refresh_token", "secret", "api_key",
        "apiKey", "authorization", "bearer", "credential", "session_token", "sessionToken",
        "auth_token", "authToken", "id_token", "idToken", "accessToken", "refreshToken",
        "user_id", "userId", "account_id", "accountId", "team_id", "teamId", "org_id", "orgId",
        "account_display_name", "accountDisplayName", "payment_id", "paymentId", "profile_arn",
        "profileArn", "email", "login", "analytics_tracking_id"
    };

    private static readonly Regex JwtRegex = new(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled);
    private static readonly Regex ApiKeyQuotedRegex = new(@"[""']?(sk-|pk-|api_|key_|secret_)[A-Za-z0-9_-]{12,}[""']?", RegexOptions.Compiled);
    private static readonly Regex ApiKeyBareRegex = new(@"(sk-|pk-|api_|key_|secret_)[A-Za-z0-9_-]{12,}", RegexOptions.Compiled);
    private static readonly Regex DevinRegex = new(@"devin-session-token\$[^\s""',}\]]+", RegexOptions.Compiled);
    private static readonly Regex AccountRegex = new(@"(account=)([^,\s]+)", RegexOptions.Compiled);
    // Windows path variant: drive-letter paths and UNC paths, plus the classic *nix roots for parity
    // with credential files that may embed a macOS-style path in imported data.
    private static readonly Regex PathRegex = new(
        @"([A-Za-z]:\\[^\s""')]+|\\\\[^\s""')]+|/(?:Users|home|opt|private|var|tmp|Applications)/[^\s""')]+)",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, Regex> JsonKeyRegexCache = JsonSensitiveKeys.ToDictionary(
        key => key,
        key => new Regex($"\"{Regex.Escape(key)}\":\\s*\"([^\"]+)\"", RegexOptions.Compiled));

    public static string RedactBody(string body)
    {
        var result = body;
        result = JwtRegex.Replace(result, m => RedactValue(m.Value));
        result = ApiKeyQuotedRegex.Replace(result, m => RedactValue(m.Value.Trim('"', '\'')));
        result = DevinRegex.Replace(result, m => RedactValue(m.Value));
        foreach (var key in JsonSensitiveKeys)
        {
            var regex = JsonKeyRegexCache[key];
            result = regex.Replace(result, m => $"\"{key}\": \"{RedactValue(m.Groups[1].Value)}\"");
        }
        result = PathRegex.Replace(result, "[PATH]");
        return result;
    }

    public static string BodyPreview(string body, int limit = 500)
    {
        var redacted = RedactBody(body);
        var redactedBytes = Encoding.UTF8.GetByteCount(redacted);
        if (redactedBytes <= limit) return redacted;

        var truncated = new StringBuilder();
        var byteOffset = 0;
        foreach (var ch in redacted)
        {
            if (byteOffset >= limit) break;
            truncated.Append(ch);
            byteOffset += Encoding.UTF8.GetByteCount(ch.ToString());
        }
        var originalBytes = Encoding.UTF8.GetByteCount(body);
        return $"{truncated}... ({originalBytes} bytes total)";
    }

    public static string RedactLogMessage(string message)
    {
        var result = message;
        result = JwtRegex.Replace(result, m => RedactValue(m.Value));
        result = ApiKeyBareRegex.Replace(result, m => RedactValue(m.Value));
        result = DevinRegex.Replace(result, m => RedactValue(m.Value));
        result = AccountRegex.Replace(result, m => $"{m.Groups[1].Value}{RedactValue(m.Groups[2].Value)}");
        result = PathRegex.Replace(result, "[PATH]");
        return result;
    }
}
