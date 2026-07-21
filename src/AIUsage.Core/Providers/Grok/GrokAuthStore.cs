using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Grok;

public sealed class GrokAuthEntry
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("refresh")] public string? Refresh { get; set; }
    [JsonPropertyName("id_token")] public string? IdToken { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("expires")] public string? Expires { get; set; }
    [JsonPropertyName("oidc_client_id")] public string? OidcClientId { get; set; }
}

public sealed class GrokAuthState
{
    public required Dictionary<string, GrokAuthEntry> Auth { get; set; }
    public required string EntryKey { get; set; }
    public required GrokAuthEntry Entry { get; set; }
    public required string Token { get; set; }
}

public enum GrokAuthErrorKind
{
    NotLoggedIn,
    InvalidAuth,
    Expired
}

public sealed class GrokAuthError : Exception, Models.ICategorizedError
{
    public GrokAuthErrorKind Kind { get; }

    public GrokAuthError(GrokAuthErrorKind kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    private static string Describe(GrokAuthErrorKind kind) => kind switch
    {
        GrokAuthErrorKind.NotLoggedIn => "Grok not logged in. Run `grok login`.",
        GrokAuthErrorKind.InvalidAuth => "Grok auth invalid. Run `grok login` again.",
        GrokAuthErrorKind.Expired => "Grok auth expired. Run `grok login` again.",
        _ => "Grok authentication error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        GrokAuthErrorKind.NotLoggedIn => Models.ErrorCategory.NotLoggedIn,
        GrokAuthErrorKind.InvalidAuth => Models.ErrorCategory.AuthInvalid,
        GrokAuthErrorKind.Expired => Models.ErrorCategory.AuthExpired,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>
/// Reads the Grok CLI's auth.json. The Grok CLI is cross-platform (Node/Bun-based) and writes
/// `%USERPROFILE%\.grok\auth.json` on Windows exactly like `~/.grok/auth.json` on macOS/Linux.
/// </summary>
public sealed class GrokAuthStore
{
    public static readonly string AuthPath = "~/.grok/auth.json";
    public const string DefaultClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    public static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    private readonly ITextFileAccessing _files;
    private readonly Func<DateTimeOffset> _now;

    public GrokAuthStore(ITextFileAccessing? files = null, Func<DateTimeOffset>? now = null)
    {
        _files = files ?? new LocalTextFileAccessor();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public List<GrokAuthState> LoadAuthCandidates()
    {
        if (!_files.Exists(AuthPath)) throw new GrokAuthError(GrokAuthErrorKind.NotLoggedIn);
        string text;
        try { text = _files.ReadText(AuthPath); } catch { throw new GrokAuthError(GrokAuthErrorKind.NotLoggedIn); }
        var auth = ParseAuth(text);
        if (auth is null) throw new GrokAuthError(GrokAuthErrorKind.NotLoggedIn);

        var candidates = new List<GrokAuthState>();
        foreach (var (entryKey, entry) in auth)
        {
            var token = Trimmed(entry.Key);
            if (token is null) continue;
            candidates.Add(new GrokAuthState { Auth = auth, EntryKey = entryKey, Entry = entry, Token = token });
        }
        if (candidates.Count == 0) throw new GrokAuthError(GrokAuthErrorKind.InvalidAuth);
        return candidates;
    }

    public void Save(GrokAuthState state)
    {
        Dictionary<string, JsonElement> authObject;
        if (_files.Exists(AuthPath))
        {
            string existingText;
            try { existingText = _files.ReadText(AuthPath); } catch { throw new GrokAuthError(GrokAuthErrorKind.InvalidAuth); }
            var parsed = ParseJsonObject(existingText);
            if (parsed is null) throw new GrokAuthError(GrokAuthErrorKind.InvalidAuth);
            authObject = parsed;
        }
        else
        {
            authObject = JsonObjectFrom(state.Auth);
        }

        var entryObject = authObject.TryGetValue(state.EntryKey, out var existingEntry) && existingEntry.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingEntry.GetRawText())!
            : new Dictionary<string, JsonElement>();

        entryObject["key"] = JsonSerializer.SerializeToElement(state.Entry.Key);
        if (state.Entry.RefreshToken is not null) entryObject["refresh_token"] = JsonSerializer.SerializeToElement(state.Entry.RefreshToken);
        if (state.Entry.IdToken is not null) entryObject["id_token"] = JsonSerializer.SerializeToElement(state.Entry.IdToken);
        if (state.Entry.ExpiresAt is not null) entryObject["expires_at"] = JsonSerializer.SerializeToElement(state.Entry.ExpiresAt);

        authObject[state.EntryKey] = JsonSerializer.SerializeToElement(entryObject);
        var text = JsonSerializer.Serialize(authObject, new JsonSerializerOptions { WriteIndented = true });
        _files.WriteText(AuthPath, text);
    }

    public bool NeedsRefresh(GrokAuthEntry entry, string token)
    {
        var entryNeedsRefresh = EntryExpiresAt(entry) is { } e && NeedsRefresh(e);
        var tokenNeedsRefresh = TokenExpiresAt(token) is { } t && NeedsRefresh(t);
        return entryNeedsRefresh || tokenNeedsRefresh;
    }

    public bool IsExpired(GrokAuthEntry entry, string token)
    {
        var expiresAt = TokenExpiresAt(token) ?? EntryExpiresAt(entry);
        return expiresAt is { } e && _now() >= e;
    }

    public string? RefreshToken(GrokAuthEntry entry) => Trimmed(entry.RefreshToken) ?? Trimmed(entry.Refresh);

    public string ClientId(string entryKey, GrokAuthEntry entry)
    {
        if (Trimmed(entry.OidcClientId) is { } oidc) return oidc;
        var parts = entryKey.Split("::");
        if (parts.Length > 0)
        {
            var value = parts[^1].Trim();
            if (value.Length > 0) return value;
        }
        return DefaultClientId;
    }

    public DateTimeOffset? TokenExpiresAt(string token)
    {
        var payload = ProviderParse.JwtPayload(token);
        if (payload is not { } p || !p.TryGetProperty("exp", out var expEl)) return null;
        var exp = ProviderParse.Number(expEl);
        return exp is { } e ? DateTimeOffset.FromUnixTimeSeconds((long)e) : null;
    }

    public static Dictionary<string, GrokAuthEntry>? ParseAuth(string text)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, GrokAuthEntry>>(text, JsonDefaults.Options); }
        catch { return null; }
    }

    public static Dictionary<string, JsonElement>? ParseJsonObject(string text)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text); }
        catch { return null; }
    }

    private DateTimeOffset? EntryExpiresAt(GrokAuthEntry entry)
    {
        if (Trimmed(entry.ExpiresAt) is { } expiresAt && AIUsageISO8601.Parse(expiresAt) is { } d1) return d1;
        if (Trimmed(entry.Expires) is { } expires && AIUsageISO8601.Parse(expires) is { } d2) return d2;
        return null;
    }

    private bool NeedsRefresh(DateTimeOffset expiresAt) => (expiresAt - _now()) <= RefreshBuffer;

    private static Dictionary<string, JsonElement> JsonObjectFrom(Dictionary<string, GrokAuthEntry> auth)
    {
        var json = JsonSerializer.Serialize(auth);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
               ?? throw new GrokAuthError(GrokAuthErrorKind.InvalidAuth);
    }

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
