using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Kiro;

/// <summary>Desktop-flavored credentials cache written by the Kiro IDE at
/// ~/.aws/sso/cache/kiro-auth-token.json (a plain JSON file, not a real AWS SSO cache entry despite
/// the location it borrows). Refreshed via Kiro's own desktop-auth endpoint.</summary>
public sealed class KiroDesktopAuth
{
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("profileArn")] public string? ProfileArn { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("expiresAt")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("clientIdHash")] public string? ClientIdHash { get; set; }
    [JsonPropertyName("authMethod")] public string? AuthMethod { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
}

/// <summary>Device-registration entry for Enterprise IdC accounts, stored alongside the desktop auth
/// file under `~/.aws/sso/cache/{clientIdHash}.json` and referenced by <see cref="KiroDesktopAuth.ClientIdHash"/>.</summary>
public sealed class KiroDeviceRegistration
{
    [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }
}

/// <summary>kiro-cli's AWS SSO OIDC token, stored in its SQLite database under the
/// `kirocli:odic:token` (or legacy `codewhisperer:odic:token`) key.</summary>
public sealed class KiroCliToken
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
}

/// <summary>Device registration (clientId/clientSecret) for kiro-cli's AWS SSO OIDC token, stored
/// under the `kirocli:odic:device-registration` (or legacy `codewhisperer:odic:device-registration`)
/// key.</summary>
public sealed class KiroCliDeviceRegistration
{
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [JsonPropertyName("client_secret")] public string? ClientSecret { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
}

/// <summary>Either credential shape, normalized to what the usage client needs: an access token, a
/// refresh path, and (once resolved) a CodeWhisperer profile ARN whose region drives the data-plane
/// host — which can differ from the SSO/auth region.</summary>
public abstract record KiroAuthSource
{
    public sealed record DesktopFile(string Path) : KiroAuthSource;
    public sealed record CliDatabase(string Path, string TokenKey, string RegistrationKey) : KiroAuthSource;
}

public sealed class KiroAuthState
{
    public required string AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? ProfileArn { get; set; }
    /// <summary>SSO/auth region — used only for the token refresh endpoint.</summary>
    public string? SsoRegion { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Present only for kiro-cli's AWS SSO OIDC flow; the desktop flow refreshes with just the refresh token.</summary>
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public required KiroAuthSource Source { get; init; }

    public bool IsCliOidc => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);
}

public enum KiroAuthErrorKind
{
    NotLoggedIn,
    SessionExpired,
    InvalidAuthPayload
}

public sealed class KiroAuthError : Exception, Models.ICategorizedError
{
    public KiroAuthErrorKind Kind { get; }

    public KiroAuthError(KiroAuthErrorKind kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    private static string Describe(KiroAuthErrorKind kind) => kind switch
    {
        KiroAuthErrorKind.NotLoggedIn => "Not logged in. Sign in to Kiro or run `kiro-cli login`.",
        KiroAuthErrorKind.SessionExpired => "Session expired. Sign in to Kiro or run `kiro-cli login` again.",
        KiroAuthErrorKind.InvalidAuthPayload => "Kiro auth data is invalid.",
        _ => "Kiro authentication error."
    };

    public bool AllowsAuthFallback => Kind == KiroAuthErrorKind.SessionExpired;

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        KiroAuthErrorKind.NotLoggedIn => Models.ErrorCategory.NotLoggedIn,
        KiroAuthErrorKind.SessionExpired => Models.ErrorCategory.AuthExpired,
        KiroAuthErrorKind.InvalidAuthPayload => Models.ErrorCategory.AuthInvalid,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>
/// Reads Kiro's local credentials. Two independent sources, checked in order:
///
/// 1. The Kiro IDE writes a plain JSON file at `~/.aws/sso/cache/kiro-auth-token.json` (it borrows
///    the AWS SSO cache directory but is not a real SSO cache entry). Enterprise/IdC logins add a
///    `clientIdHash` pointing at a sibling device-registration file in the same directory. Refreshed
///    via Kiro's own desktop-auth endpoint (just a refresh token, no client secret).
/// 2. `kiro-cli` keeps its session in a SQLite database at `%LOCALAPPDATA%\Kiro-Cli\data.sqlite3` on
///    Windows (the counterpart of `~/.local/share/kiro-cli/data.sqlite3` on Linux/macOS), table
///    `auth_kv`, under `kirocli:odic:token` (AWS SSO OIDC — needs the paired
///    `kirocli:odic:device-registration` clientId/clientSecret to refresh) or `kirocli:social:token`
///    (social login). Both the current and legacy (`codewhisperer:...`) key names are probed.
///
/// Both sources can disagree between their SSO/auth region and their CodeWhisperer profile's
/// data-plane region — the profile ARN's region is authoritative for API calls; the stored region is
/// only used to reach the refresh endpoint.
/// </summary>
public sealed class KiroAuthStore
{
    private static readonly string[] CliTokenKeys = { "kirocli:social:token", "kirocli:odic:token", "codewhisperer:odic:token" };
    private static readonly string[] CliRegistrationKeys = { "kirocli:odic:device-registration", "codewhisperer:odic:device-registration" };

    public static readonly string DesktopAuthPath = "~/.aws/sso/cache/kiro-auth-token.json";
    public static readonly string CliDatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiro-Cli", "data.sqlite3");

    private readonly ITextFileAccessing _files;
    private readonly ISqliteAccessing _sqlite;

    public KiroAuthStore(ITextFileAccessing? files = null, ISqliteAccessing? sqlite = null)
    {
        _files = files ?? new LocalTextFileAccessor();
        _sqlite = sqlite ?? new SqliteDataAccessor();
    }

    /// <summary>All available credential states, desktop file first (it's the richer, more commonly
    /// used source), then the CLI database.</summary>
    public List<KiroAuthState> LoadAuthCandidates()
    {
        var candidates = new List<KiroAuthState>();
        if (LoadDesktopAuth() is { } desktop) candidates.Add(desktop);
        if (LoadCliAuth() is { } cli) candidates.Add(cli);
        return candidates;
    }

    public KiroAuthState? LoadDesktopAuth()
    {
        if (!_files.Exists(DesktopAuthPath)) return null;
        string text;
        try { text = _files.ReadText(DesktopAuthPath); } catch { return null; }

        var auth = ProviderParse.DecodeJsonWithHexFallback<KiroDesktopAuth>(text);
        if (auth is null || string.IsNullOrEmpty(auth.AccessToken)) return null;

        string? clientId = null, clientSecret = null;
        if (!string.IsNullOrEmpty(auth.ClientIdHash))
        {
            var registrationPath = $"~/.aws/sso/cache/{auth.ClientIdHash}.json";
            if (_files.Exists(registrationPath))
            {
                try
                {
                    var registration = ProviderParse.DecodeJsonWithHexFallback<KiroDeviceRegistration>(_files.ReadText(registrationPath));
                    clientId = registration?.ClientId;
                    clientSecret = registration?.ClientSecret;
                }
                catch
                {
                    // Enterprise device registration is optional context for refresh; the desktop
                    // endpoint only strictly needs the refresh token.
                }
            }
        }

        return new KiroAuthState
        {
            AccessToken = auth.AccessToken!,
            RefreshToken = auth.RefreshToken,
            ProfileArn = auth.ProfileArn,
            SsoRegion = auth.Region,
            ExpiresAt = auth.ExpiresAt is { } exp ? AIUsageISO8601.Parse(exp) : null,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Source = new KiroAuthSource.DesktopFile(DesktopAuthPath)
        };
    }

    public KiroAuthState? LoadCliAuth()
    {
        if (!_files.Exists(CliDatabasePath)) return null;

        foreach (var tokenKey in CliTokenKeys)
        {
            string? tokenJson;
            try { tokenJson = _sqlite.QueryValue(CliDatabasePath, $"SELECT value FROM auth_kv WHERE key = '{tokenKey}'"); }
            catch { continue; }
            if (tokenJson is null) continue;

            var token = ProviderParse.DecodeJsonWithHexFallback<KiroCliToken>(tokenJson);
            if (token is null || string.IsNullOrEmpty(token.AccessToken)) continue;

            string? registrationKey = tokenKey switch
            {
                "kirocli:odic:token" => "kirocli:odic:device-registration",
                "codewhisperer:odic:token" => "codewhisperer:odic:device-registration",
                _ => null
            };

            string? clientId = null, clientSecret = null;
            if (registrationKey is not null)
            {
                try
                {
                    var regJson = _sqlite.QueryValue(CliDatabasePath, $"SELECT value FROM auth_kv WHERE key = '{registrationKey}'");
                    if (regJson is not null)
                    {
                        var registration = ProviderParse.DecodeJsonWithHexFallback<KiroCliDeviceRegistration>(regJson);
                        clientId = registration?.ClientId;
                        clientSecret = registration?.ClientSecret;
                    }
                }
                catch
                {
                    // Missing registration just disables refresh for this session's token; the
                    // current access token can still serve one probe.
                }
            }

            return new KiroAuthState
            {
                AccessToken = token.AccessToken!,
                RefreshToken = token.RefreshToken,
                ProfileArn = ProfileArnFromState(),
                SsoRegion = token.Region,
                ExpiresAt = token.ExpiresAt is { } exp ? AIUsageISO8601.Parse(exp) : null,
                ClientId = clientId,
                ClientSecret = clientSecret,
                Source = new KiroAuthSource.CliDatabase(CliDatabasePath, tokenKey, registrationKey ?? "")
            };
        }

        return null;
    }

    /// <summary>kiro-cli caches the resolved CodeWhisperer profile ARN in its `state` table
    /// (`api.codewhisperer.profile`), which is where the data-plane region actually comes from — it
    /// can differ from the SSO region above.</summary>
    private string? ProfileArnFromState()
    {
        string? json;
        try { json = _sqlite.QueryValue(CliDatabasePath, "SELECT value FROM state WHERE key = 'api.codewhisperer.profile'"); }
        catch { return null; }
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("arn", out var arnEl) && arnEl.ValueKind == JsonValueKind.String
                ? arnEl.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extract the CodeWhisperer data-plane region from a profile ARN
    /// (`arn:aws:codewhisperer:{region}:...`). The SSO/auth region is *not* a usable fallback here —
    /// accounts can authenticate through an SSO region (e.g. `eu-west-1`) that has no corresponding
    /// `q.{region}.amazonaws.com` data-plane host at all. Absent a resolved profile ARN, `us-east-1`
    /// is the only region guaranteed to answer (it's also where profile discovery itself is called).</summary>
    public static string DataPlaneRegion(KiroAuthState state) => RegionFromProfileArn(state.ProfileArn) ?? "us-east-1";

    private static string? RegionFromProfileArn(string? profileArn)
    {
        if (string.IsNullOrEmpty(profileArn)) return null;
        var parts = profileArn.Split(':');
        return parts.Length >= 4 && parts[0] == "arn" && parts[2] == "codewhisperer" ? parts[3].NilIfEmpty() : null;
    }

    public void SaveRefreshedToken(KiroAuthState state, string accessToken, string? refreshToken, string? profileArn)
    {
        state.AccessToken = accessToken;
        if (!string.IsNullOrEmpty(refreshToken)) state.RefreshToken = refreshToken;
        if (!string.IsNullOrEmpty(profileArn)) state.ProfileArn = profileArn;

        switch (state.Source)
        {
            case KiroAuthSource.DesktopFile f:
                SaveDesktopFile(f.Path, state);
                break;
            case KiroAuthSource.CliDatabase db:
                SaveCliToken(db, state);
                break;
        }
    }

    private void SaveDesktopFile(string path, KiroAuthState state)
    {
        // Read-merge-write: preserve every field the IDE wrote (authMethod, provider, clientIdHash, ...)
        // and only update the fields we actually refreshed.
        string existingText;
        try { existingText = _files.ReadText(path); }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.AuthFor("kiro"), $"failed to re-read desktop auth file before write-back: {ex.Message}");
            return;
        }

        var existing = ProviderParse.DecodeJsonWithHexFallback<KiroDesktopAuth>(existingText) ?? new KiroDesktopAuth();
        existing.AccessToken = state.AccessToken;
        if (state.RefreshToken is not null) existing.RefreshToken = state.RefreshToken;
        if (state.ProfileArn is not null) existing.ProfileArn = state.ProfileArn;
        if (state.ExpiresAt is { } exp) existing.ExpiresAt = AIUsageISO8601.ToStringIso(exp);

        try
        {
            _files.WriteText(path, JsonSerializer.Serialize(existing, JsonDefaults.Options));
        }
        catch (Exception ex)
        {
            AppLog.Error(LogTag.AuthFor("kiro"), $"failed to persist refreshed desktop credentials; using the refreshed token for this session only: {ex.Message}");
        }
    }

    private void SaveCliToken(KiroAuthSource.CliDatabase db, KiroAuthState state)
    {
        string? existingJson;
        try { existingJson = _sqlite.QueryValue(db.Path, $"SELECT value FROM auth_kv WHERE key = '{db.TokenKey}'"); }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.AuthFor("kiro"), $"failed to re-read kiro-cli token before write-back: {ex.Message}");
            return;
        }
        if (existingJson is null) return;

        try
        {
            // Read-merge-write: preserve every field kiro-cli wrote (start_url, oauth_flow, scopes,
            // ...) and only update the fields we actually refreshed.
            using var doc = JsonDocument.Parse(existingJson);
            var flattened = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                flattened[prop.Name] = JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
            }
            flattened["access_token"] = state.AccessToken;
            if (state.RefreshToken is not null) flattened["refresh_token"] = state.RefreshToken;
            if (state.ExpiresAt is { } expiresAt) flattened["expires_at"] = AIUsageISO8601.ToStringIso(expiresAt);
            if (state.SsoRegion is not null) flattened["region"] = state.SsoRegion;

            var sql = $"UPDATE auth_kv SET value = '{JsonSerializer.Serialize(flattened).Replace("'", "''")}' WHERE key = '{db.TokenKey}'";
            _sqlite.Execute(db.Path, sql);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogTag.AuthFor("kiro"), $"failed to persist refreshed kiro-cli credentials; using the refreshed token for this session only: {ex.Message}");
        }
    }

    public bool NeedsRefresh(KiroAuthState state, Func<DateTimeOffset> now)
    {
        if (state.ExpiresAt is not { } expiresAt) return false;
        return (expiresAt - now()) <= TimeSpan.FromMinutes(5);
    }
}
