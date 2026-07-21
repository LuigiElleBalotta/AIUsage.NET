using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Cursor;

public abstract record CursorAuthSource
{
    public sealed record Sqlite : CursorAuthSource;
    public sealed record Keychain : CursorAuthSource;
}

public sealed class CursorAuthState
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public required CursorAuthSource Source { get; init; }
}

public enum CursorAuthErrorKind
{
    NotLoggedIn,
    SessionExpired,
    TokenExpired
}

public sealed class CursorAuthError : Exception, Models.ICategorizedError
{
    public CursorAuthErrorKind Kind { get; }

    public CursorAuthError(CursorAuthErrorKind kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    private static string Describe(CursorAuthErrorKind kind) => kind switch
    {
        CursorAuthErrorKind.NotLoggedIn => "Not logged in. Sign in via the Cursor app or run `agent login`.",
        CursorAuthErrorKind.SessionExpired => "Session expired. Sign in via the Cursor app or run `agent login`.",
        CursorAuthErrorKind.TokenExpired => "Token expired. Sign in via the Cursor app or run `agent login`.",
        _ => "Cursor authentication error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        CursorAuthErrorKind.NotLoggedIn => Models.ErrorCategory.NotLoggedIn,
        CursorAuthErrorKind.SessionExpired or CursorAuthErrorKind.TokenExpired => Models.ErrorCategory.AuthExpired,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>
/// Reads the Cursor editor's stored auth. Cursor (a cross-platform Electron-based editor) writes its
/// `state.vscdb` under `%APPDATA%\Cursor\User\globalStorage` on Windows (the direct counterpart of
/// `~/Library/Application Support/Cursor/...` on macOS), read here via Microsoft.Data.Sqlite instead
/// of the macOS `sqlite3` CLI. Windows Credential Manager is probed as the secondary source, matching
/// the macOS Keychain fallback.
/// </summary>
public sealed class CursorAuthStore
{
    public static readonly string StateDbPath = "%APPDATA%/Cursor/User/globalStorage/state.vscdb";
    public const string AccessTokenKey = "cursorAuth/accessToken";
    public const string RefreshTokenKey = "cursorAuth/refreshToken";
    public const string MembershipTypeKey = "cursorAuth/stripeMembershipType";
    public const string KeychainAccessTokenService = "cursor-access-token";
    public const string KeychainRefreshTokenService = "cursor-refresh-token";
    public static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    private readonly ISqliteAccessing _sqlite;
    private readonly IKeychainAccessing _keychain;
    private readonly Func<DateTimeOffset> _now;

    public CursorAuthStore(
        ISqliteAccessing? sqlite = null,
        IKeychainAccessing? keychain = null,
        Func<DateTimeOffset>? now = null)
    {
        _sqlite = sqlite ?? new SqliteDataAccessor();
        _keychain = keychain ?? new WindowsCredentialAccessor();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private static string ResolvedStateDbPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Cursor", "User", "globalStorage", "state.vscdb");
    }

    public CursorAuthState? LoadAuthState()
    {
        var sqliteAccessToken = ReadStateValue(AccessTokenKey);
        var sqliteRefreshToken = ReadStateValue(RefreshTokenKey);
        var sqliteMembershipType = ReadStateValue(MembershipTypeKey)?.Trim().ToLowerInvariant();

        var keychainAccessToken = ReadKeychainValue(KeychainAccessTokenService);
        var keychainRefreshToken = ReadKeychainValue(KeychainRefreshTokenService);

        var hasSqliteAuth = sqliteAccessToken is not null || sqliteRefreshToken is not null;
        var hasKeychainAuth = keychainAccessToken is not null || keychainRefreshToken is not null;

        if (hasSqliteAuth)
        {
            var sqliteSubject = TokenSubject(sqliteAccessToken);
            var keychainSubject = TokenSubject(keychainAccessToken);
            var subjectsDiffer = sqliteSubject is not null && keychainSubject is not null && sqliteSubject != keychainSubject;
            if (hasKeychainAuth && sqliteMembershipType == "free" && subjectsDiffer)
            {
                return new CursorAuthState { AccessToken = keychainAccessToken, RefreshToken = keychainRefreshToken, Source = new CursorAuthSource.Keychain() };
            }
            return new CursorAuthState { AccessToken = sqliteAccessToken, RefreshToken = sqliteRefreshToken, Source = new CursorAuthSource.Sqlite() };
        }

        if (hasKeychainAuth)
        {
            return new CursorAuthState { AccessToken = keychainAccessToken, RefreshToken = keychainRefreshToken, Source = new CursorAuthSource.Keychain() };
        }

        return null;
    }

    public bool NeedsRefresh(string? accessToken)
    {
        if (string.IsNullOrEmpty(accessToken) || TokenExpiration(accessToken) is not { } expiresAt) return true;
        return (expiresAt - _now()) <= RefreshBuffer;
    }

    public void SaveAccessToken(string accessToken, CursorAuthSource source)
    {
        switch (source)
        {
            case CursorAuthSource.Sqlite:
                WriteStateValue(AccessTokenKey, accessToken);
                break;
            case CursorAuthSource.Keychain:
                _keychain.WriteGenericPassword(KeychainAccessTokenService, accessToken);
                break;
        }
    }

    private string? ReadStateValue(string key)
    {
        var sql = $"SELECT value FROM ItemTable WHERE key = '{SqlEscaped(key)}' LIMIT 1;";
        string? value;
        try { value = _sqlite.QueryValue(ResolvedStateDbPath(), sql); } catch { return null; }
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private void WriteStateValue(string key, string value)
    {
        var sql = $"INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('{SqlEscaped(key)}', '{SqlEscaped(value)}');";
        _sqlite.Execute(ResolvedStateDbPath(), sql);
    }

    private string? ReadKeychainValue(string service)
    {
        string? value;
        try { value = _keychain.ReadGenericPassword(service); } catch { return null; }
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static DateTimeOffset? TokenExpiration(string token)
    {
        var payload = ProviderParse.JwtPayload(token);
        if (payload is not { } p || !p.TryGetProperty("exp", out var expEl)) return null;
        var exp = ProviderParse.Number(expEl);
        return exp is { } e ? DateTimeOffset.FromUnixTimeSeconds((long)e) : null;
    }

    public static string? TokenSubject(string? token)
    {
        if (token is null) return null;
        var payload = ProviderParse.JwtPayload(token);
        if (payload is not { } p || !p.TryGetProperty("sub", out var subEl) || subEl.ValueKind != System.Text.Json.JsonValueKind.String) return null;
        var trimmed = subEl.GetString()?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string SqlEscaped(string value) => value.Replace("'", "''");
}
