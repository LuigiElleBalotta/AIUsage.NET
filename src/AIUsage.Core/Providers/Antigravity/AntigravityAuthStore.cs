using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Antigravity;

public sealed record AntigravityKeychainToken(string? AccessToken, string? RefreshToken, DateTimeOffset? Expiry);

/// <summary>
/// Credentials Antigravity already has on the machine. Windows port note: on macOS the OAuth tokens
/// live in the Keychain (service "gemini", account "antigravity") as a go-keyring-base64-wrapped JSON
/// blob. On Windows, Antigravity/agy store the equivalent secret in Windows Credential Manager under
/// the same service/account naming (go-keyring wraps identically cross-platform via the same Go
/// library), read here through WindowsCredentialAccessor instead of the `security` CLI.
/// </summary>
public sealed class AntigravityAuthStore
{
    public const string KeychainService = "gemini";
    public const string KeychainAccount = "antigravity";
    public static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsage", "antigravity", "auth.json");
    public static readonly TimeSpan RefreshBuffer = TimeSpan.FromSeconds(60);

    private readonly IKeychainAccessing _keychain;
    private readonly ITextFileAccessing _files;
    private readonly Func<DateTimeOffset> _now;

    public AntigravityAuthStore(IKeychainAccessing? keychain = null, ITextFileAccessing? files = null, Func<DateTimeOffset>? now = null)
    {
        _keychain = keychain ?? new WindowsCredentialAccessor();
        _files = files ?? new LocalTextFileAccessor();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public AntigravityKeychainToken? LoadKeychainToken()
    {
        string? raw;
        try
        {
            raw = _keychain.ReadGenericPassword(KeychainService, KeychainAccount);
        }
        catch (Exception)
        {
            AppLog.Error(LogTag.AuthFor("antigravity"), "keychain credential read failed");
            throw new AntigravityError(AntigravityErrorKind.CredentialStoreUnreadable);
        }
        if (raw is null) return null;
        var token = ExtractToken(raw);
        if (token is null)
        {
            AppLog.Error(LogTag.AuthFor("antigravity"), "keychain credential is malformed");
            throw new AntigravityError(AntigravityErrorKind.InvalidCredentialData);
        }
        return token;
    }

    public bool IsUsable(DateTimeOffset? expiry)
    {
        if (expiry is not { } e) return true;
        return (e - _now()) > RefreshBuffer;
    }

    private sealed class CachedToken
    {
        public string AccessToken { get; set; } = "";
        public double ExpiresAtMs { get; set; }
        public string? CredentialFingerprint { get; set; }
    }

    public string? LoadCachedToken(AntigravityKeychainToken source)
    {
        var expectedFingerprint = CredentialFingerprint(source.RefreshToken);
        if (expectedFingerprint is null) { DiscardCachedToken(); return null; }

        string? text;
        try { text = _files.ReadTextIfPresent(CachePath); }
        catch
        {
            AppLog.Warn(LogTag.AuthFor("antigravity"), "refreshed-token cache read failed; ignoring it");
            return null;
        }
        if (text is null) return null;

        CachedToken? cached;
        try { cached = JsonSerializer.Deserialize<CachedToken>(text); }
        catch
        {
            AppLog.Warn(LogTag.AuthFor("antigravity"), "refreshed-token cache is malformed; discarding it");
            DiscardCachedToken();
            return null;
        }
        if (cached is null || cached.CredentialFingerprint != expectedFingerprint) { DiscardCachedToken(); return null; }
        if (cached.ExpiresAtMs <= (_now().ToUnixTimeMilliseconds() + RefreshBuffer.TotalMilliseconds)) { DiscardCachedToken(); return null; }
        var token = cached.AccessToken.Trim().NilIfEmpty();
        if (token is null) { DiscardCachedToken(); return null; }
        return token;
    }

    public void CacheToken(string accessToken, double expiresIn, string sourceRefreshToken)
    {
        var fingerprint = CredentialFingerprint(sourceRefreshToken);
        if (fingerprint is null || string.IsNullOrWhiteSpace(accessToken)) return;

        var expiresAtMs = (_now().ToUnixTimeMilliseconds()) + expiresIn * 1000;
        var cached = new CachedToken { AccessToken = accessToken, ExpiresAtMs = expiresAtMs, CredentialFingerprint = fingerprint };
        try
        {
            _files.WriteText(CachePath, JsonSerializer.Serialize(cached));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.AuthFor("antigravity"), $"failed to cache refreshed token: {ex.Message}");
        }
    }

    public void DiscardCachedToken()
    {
        try { _files.Remove(CachePath); }
        catch { AppLog.Warn(LogTag.AuthFor("antigravity"), "failed to remove stale refreshed-token cache"); }
    }

    private static string? CredentialFingerprint(string? refreshToken)
    {
        var trimmed = refreshToken?.Trim().NilIfEmpty();
        if (trimmed is null) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        return Convert.ToHexString(hash);
    }

    // MARK: - Token extraction (pure)

    public static AntigravityKeychainToken? ExtractToken(string raw)
    {
        var normalizedRaw = raw.Trim().Trim('\uFEFF');
        var unwrapped = ProviderParse.UnwrapGoKeyring(normalizedRaw);
        if (unwrapped is null) return null;
        var text = unwrapped.Trim().Trim('\uFEFF').NilIfEmpty();
        if (text is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return TokenFromObject(doc.RootElement.Clone());
            }
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                var s = doc.RootElement.GetString()?.Trim().NilIfEmpty();
                return s is not null ? new AntigravityKeychainToken(s, null, null) : null;
            }
            return null;
        }
        catch
        {
            if (text.StartsWith('{') || text.StartsWith('[')) return null;
            if (text.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                var token = text["Bearer ".Length..].Trim().NilIfEmpty();
                return token is not null ? new AntigravityKeychainToken(token, null, null) : null;
            }
            return new AntigravityKeychainToken(text, null, null);
        }
    }

    public static AntigravityKeychainToken? TokenFromObject(JsonElement obj)
    {
        var source = obj.TryGetProperty("token", out var tokenObj) && tokenObj.ValueKind == JsonValueKind.Object ? tokenObj : obj;
        var access = FirstString(source, "access_token", "accessToken", "token", "id_token", "idToken", "bearerToken", "auth_token", "authToken");
        var refresh = FirstString(source, "refresh_token", "refreshToken");
        var expiryRaw = FirstString(source, "expiry", "expires_at", "expiresAt");
        var expiry = expiryRaw is not null ? AIUsageISO8601.Parse(expiryRaw) : null;

        if (access is null && refresh is null)
        {
            foreach (var key in new[] { "tokens", "oauth", "oauth2", "credentials", "auth" })
            {
                if (obj.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Object)
                {
                    if (TokenFromObject(nested) is { } token) return token;
                }
            }
            return null;
        }
        return new AntigravityKeychainToken(access, refresh, expiry);
    }

    private static string? FirstString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var value = v.GetString()?.Trim().NilIfEmpty();
                if (value is not null) return value;
            }
        }
        return null;
    }
}
