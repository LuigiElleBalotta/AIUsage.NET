using System.Text.Json;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

public enum OpenCodeCredentialKind
{
    OAuth,
    ApiKey
}

public enum OpenCodeCredentialStatus
{
    None,
    Usable,
    Expired,
    Invalid,
    UntrustedOrigin,
    Unreadable
}

/// <summary>
/// A read-only Console credential discovered on disk. <see cref="ToString"/> deliberately omits the
/// token so an accidental log or exception interpolation can never leak it.
/// </summary>
public sealed class OpenCodeCredential
{
    public OpenCodeCredential(
        OpenCodeCredentialKind kind,
        string token,
        string? orgId,
        DateTimeOffset? expiresAt,
        Uri origin)
    {
        Kind = kind;
        Token = token;
        OrgId = orgId;
        ExpiresAt = expiresAt;
        Origin = origin;
    }

    public OpenCodeCredentialKind Kind { get; }
    public string Token { get; }
    public string? OrgId { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public Uri Origin { get; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiresAt && expiresAt <= now;

    public override string ToString() =>
        $"OpenCodeCredential({Kind}, org={(string.IsNullOrEmpty(OrgId) ? "none" : "present")}, expires={(ExpiresAt is null ? "unknown" : AIUsageISO8601.ToStringIso(ExpiresAt.Value))})";
}

public sealed record OpenCodeCredentialState(
    OpenCodeCredentialStatus Status,
    OpenCodeCredential? Credential = null,
    string? Detail = null)
{
    public static readonly OpenCodeCredentialState Empty = new(OpenCodeCredentialStatus.None);

    public bool HasCredential => Credential is not null;
    public bool IsUsable => Status == OpenCodeCredentialStatus.Usable && Credential is not null;
}

/// <summary>
/// The only origin AIUsage.NET will send an OpenCode credential to. Both the canonical
/// <c>https://opencode.ai</c> base and the retired <c>https://console.opencode.ai</c> alias map to the
/// canonical origin; anything else in a credential's metadata is rejected before any request is made.
/// </summary>
public static class OpenCodeConsoleOrigin
{
    public static readonly Uri Canonical = new("https://opencode.ai");

    public static Uri? Normalize(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return Canonical;
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.Port != 443) return null;
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;

        var host = uri.Host.ToLowerInvariant();
        if (host == "console.opencode.ai") return Canonical;
        if (host != "opencode.ai") return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0 || path.Equals("/console", StringComparison.OrdinalIgnoreCase)) return Canonical;
        return null;
    }

    public static bool IsCanonical(Uri origin) =>
        origin.Scheme == Uri.UriSchemeHttps
        && origin.Host.Equals("opencode.ai", StringComparison.OrdinalIgnoreCase)
        && origin.Port == 443
        && string.IsNullOrEmpty(origin.UserInfo)
        && string.IsNullOrEmpty(origin.Query)
        && string.IsNullOrEmpty(origin.Fragment)
        && (origin.AbsolutePath == "/" || origin.AbsolutePath.Length == 0);
}

internal static class OpenCodeCredentialParser
{
    public static bool TryParse(
        JsonElement entry,
        DateTimeOffset now,
        out OpenCodeCredential? credential,
        out OpenCodeCredentialStatus failure)
    {
        credential = null;
        failure = OpenCodeCredentialStatus.Invalid;

        if (entry.ValueKind != JsonValueKind.Object) return false;

        var type = StringProperty(entry, "type")?.Trim().ToLowerInvariant();
        var token = type switch
        {
            "oauth" => StringProperty(entry, "access") ?? StringProperty(entry, "accessToken"),
            "key" or "api" => StringProperty(entry, "key") ?? StringProperty(entry, "apiKey"),
            _ => StringProperty(entry, "access") ?? StringProperty(entry, "accessToken")
                 ?? StringProperty(entry, "key") ?? StringProperty(entry, "apiKey")
        };
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();
        if (!IsSafeHeaderValue(token)) return false;

        var kind = type switch
        {
            "key" or "api" => OpenCodeCredentialKind.ApiKey,
            "oauth" => OpenCodeCredentialKind.OAuth,
            _ => StringProperty(entry, "key") is not null ? OpenCodeCredentialKind.ApiKey : OpenCodeCredentialKind.OAuth
        };

        var metadata = ObjectProperty(entry, "metadata");
        var serverRaw = metadata is { } m ? StringProperty(m, "server") : null;
        var origin = OpenCodeConsoleOrigin.Normalize(serverRaw);
        if (origin is null)
        {
            failure = OpenCodeCredentialStatus.UntrustedOrigin;
            return false;
        }

        var orgId = metadata is { } metadataObject
            ? ScalarProperty(metadataObject, "orgID")
              ?? ScalarProperty(metadataObject, "orgId")
              ?? ScalarProperty(metadataObject, "organizationID")
              ?? ScalarProperty(metadataObject, "organizationId")
            : null;
        if (!string.IsNullOrWhiteSpace(orgId) && !IsSafeHeaderValue(orgId.Trim())) return false;

        DateTimeOffset? expiresAt = null;
        var expiresElement = Property(entry, "expires") ?? Property(entry, "expiresAt");
        if (expiresElement is { } exp)
        {
            expiresAt = ParseExpiry(exp);
            if (expiresAt is null) return false;
        }

        credential = new OpenCodeCredential(kind, token, string.IsNullOrWhiteSpace(orgId) ? null : orgId.Trim(), expiresAt, origin);
        failure = credential.IsExpired(now) ? OpenCodeCredentialStatus.Expired : OpenCodeCredentialStatus.Usable;
        return true;
    }

    internal static bool IsSafeHeaderValue(string value) =>
        value.Length > 0 && value.All(c => !char.IsControl(c) && !char.IsWhiteSpace(c));

    private static DateTimeOffset? ParseExpiry(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && AIUsageISO8601.Parse(element.GetString() ?? "") is { } iso)
        {
            return iso;
        }

        var number = ProviderParse.Number(element);
        if (number is not { } value || !double.IsFinite(value) || value < 0) return null;
        if (value > 253_402_300_799_000d) return null; // milliseconds beyond year 9999
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)value);
        }
        catch
        {
            return null;
        }
    }

    internal static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty(name, out var exact)) return exact;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        return null;
    }

    internal static string? StringProperty(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value is { ValueKind: JsonValueKind.String } ? value.Value.GetString() : null;
    }

    internal static string? ScalarProperty(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            _ => null
        };
    }

    internal static JsonElement? ObjectProperty(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value is { ValueKind: JsonValueKind.Object } ? value.Value : null;
    }
}
