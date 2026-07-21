using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Codex;

public sealed class CodexTokens
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("id_token")] public string? IdToken { get; set; }
    [JsonPropertyName("account_id")] public string? AccountId { get; set; }
}

public sealed class CodexAuth
{
    [JsonPropertyName("tokens")] public CodexTokens? Tokens { get; set; }
    [JsonPropertyName("last_refresh")] public string? LastRefresh { get; set; }
    [JsonPropertyName("OPENAI_API_KEY")] public string? ApiKey { get; set; }
}

public abstract record CodexAuthSource
{
    public sealed record File(string Path) : CodexAuthSource;
    public sealed record Keychain : CodexAuthSource;

    public bool IsFile => this is File;
}

public sealed class CodexAuthState
{
    public required CodexAuth Auth { get; set; }
    public required CodexAuthSource Source { get; init; }

    public bool HasUsableAccessToken => !string.IsNullOrEmpty(Auth.Tokens?.AccessToken);
}

public enum CodexAuthErrorKind
{
    NotLoggedIn,
    SessionExpired,
    TokenConflict,
    TokenRevoked,
    TokenExpired,
    UsageApiKey,
    InvalidAuthPayload
}

public sealed class CodexAuthError : Exception, Models.ICategorizedError
{
    public CodexAuthErrorKind Kind { get; }

    public CodexAuthError(CodexAuthErrorKind kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    private static string Describe(CodexAuthErrorKind kind) => kind switch
    {
        CodexAuthErrorKind.NotLoggedIn => "Not logged in. Run `codex` to authenticate.",
        CodexAuthErrorKind.SessionExpired => "Session expired. Run `codex` to log in again.",
        CodexAuthErrorKind.TokenConflict => "Token conflict. Run `codex` to log in again.",
        CodexAuthErrorKind.TokenRevoked => "Token revoked. Run `codex` to log in again.",
        CodexAuthErrorKind.TokenExpired => "Token expired. Run `codex` to log in again.",
        CodexAuthErrorKind.UsageApiKey => "Usage not available for API key.",
        CodexAuthErrorKind.InvalidAuthPayload => "Codex auth data is invalid.",
        _ => "Codex authentication error."
    };

    public bool AllowsAuthFallback => Kind is CodexAuthErrorKind.SessionExpired or CodexAuthErrorKind.TokenConflict
        or CodexAuthErrorKind.TokenRevoked or CodexAuthErrorKind.TokenExpired;

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        CodexAuthErrorKind.NotLoggedIn => Models.ErrorCategory.NotLoggedIn,
        CodexAuthErrorKind.SessionExpired or CodexAuthErrorKind.TokenConflict or CodexAuthErrorKind.TokenRevoked or CodexAuthErrorKind.TokenExpired => Models.ErrorCategory.AuthExpired,
        CodexAuthErrorKind.UsageApiKey => Models.ErrorCategory.NotAvailable,
        CodexAuthErrorKind.InvalidAuthPayload => Models.ErrorCategory.AuthInvalid,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>
/// Reads the Codex CLI's credentials. The `codex` CLI (a cross-platform Rust/Node tool) writes
/// `auth.json` under `%USERPROFILE%\.codex` (or `%CODEX_HOME%`) on Windows exactly like it does under
/// `~/.codex` on macOS/Linux, so the file path is the primary and reliable source. Windows Credential
/// Manager is probed as a secondary source in case a future version stores it there.
/// </summary>
public sealed class CodexAuthStore
{
    public const string KeychainService = "Codex Auth";
    public static readonly TimeSpan AccessTokenRefreshWindow = TimeSpan.FromMinutes(5);
    private const string AuthFile = "auth.json";
    private static readonly string[] DefaultAuthHomes = { "~/.codex" };

    private readonly IEnvironmentReading _environment;
    private readonly ITextFileAccessing _files;
    private readonly IKeychainAccessing _keychain;
    private readonly Func<DateTimeOffset> _now;

    public CodexAuthStore(
        IEnvironmentReading? environment = null,
        ITextFileAccessing? files = null,
        IKeychainAccessing? keychain = null,
        Func<DateTimeOffset>? now = null)
    {
        _environment = environment ?? new ProcessEnvironmentReader();
        _files = files ?? new LocalTextFileAccessor();
        _keychain = keychain ?? new WindowsCredentialAccessor();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public List<CodexAuthState> LoadAuthCandidates() => AuthPaths().Select(LoadAuth).Where(s => s is not null).Select(s => s!).ToList();

    public CodexAuthState? LoadAuth(string path)
    {
        if (!_files.Exists(path)) return null;
        string text;
        try { text = _files.ReadText(path); } catch { return null; }
        var auth = ParseAuth(text);
        if (auth is null || !HasTokenLikeAuth(auth)) return null;
        return new CodexAuthState { Auth = auth, Source = new CodexAuthSource.File(path) };
    }

    public CodexAuthState? LoadKeychainAuth()
    {
        string? value;
        try { value = _keychain.ReadGenericPassword(KeychainService); } catch { return null; }
        if (value is null) return null;
        var auth = ParseAuth(value);
        if (auth is null || !HasTokenLikeAuth(auth)) return null;
        return new CodexAuthState { Auth = auth, Source = new CodexAuthSource.Keychain() };
    }

    public void Save(CodexAuthState state)
    {
        var text = JsonSerializer.Serialize(state.Auth, new JsonSerializerOptions { WriteIndented = state.Source.IsFile });
        switch (state.Source)
        {
            case CodexAuthSource.File f:
                _files.WriteText(f.Path, text);
                break;
            case CodexAuthSource.Keychain:
                _keychain.WriteGenericPassword(KeychainService, text);
                break;
        }
    }

    public bool NeedsRefresh(CodexAuth auth)
    {
        if (auth.Tokens?.AccessToken is { } accessToken && AccessTokenExpiresAt(accessToken) is { } expiresAt)
        {
            return (expiresAt - _now()) <= AccessTokenRefreshWindow;
        }
        if (auth.LastRefresh is not { } lastRefresh || AIUsageISO8601.Parse(lastRefresh) is not { } date)
        {
            return false;
        }
        return (_now() - date).TotalDays > 8;
    }

    public DateTimeOffset? AccessTokenExpiresAt(string token)
    {
        var payload = ProviderParse.JwtPayload(token);
        if (payload is not { } p || !p.TryGetProperty("exp", out var expEl)) return null;
        var exp = ProviderParse.Number(expEl);
        return exp is { } e ? DateTimeOffset.FromUnixTimeSeconds((long)e) : null;
    }

    public List<string> AuthPaths()
    {
        var codexHome = CodexHome();
        if (codexHome is not null) return new List<string> { JoinPath(codexHome, AuthFile) };
        return DefaultAuthHomes.Select(h => JoinPath(h, AuthFile)).ToList();
    }

    public string? CodexHome() => _environment.Value("CODEX_HOME")?.Trim().NilIfEmpty();

    public static CodexAuth? ParseAuth(string text) => ProviderParse.DecodeJsonWithHexFallback<CodexAuth>(text);

    public static bool HasTokenLikeAuth(CodexAuth auth) =>
        !string.IsNullOrEmpty(auth.Tokens?.AccessToken) || !string.IsNullOrEmpty(auth.ApiKey);

    private static string JoinPath(string basev, string leaf) => basev.TrimmingTrailingSlashes() + "/" + leaf;
}
