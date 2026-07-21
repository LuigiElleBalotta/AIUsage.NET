using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Claude;

public sealed class ClaudeOAuth
{
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expiresAt")] public double? ExpiresAt { get; set; }
    [JsonPropertyName("subscriptionType")] public string? SubscriptionType { get; set; }
    [JsonPropertyName("rateLimitTier")] public string? RateLimitTier { get; set; }
    [JsonPropertyName("scopes")] public List<string>? Scopes { get; set; }
}

public sealed class ClaudeCredentialsFile
{
    [JsonPropertyName("claudeAiOauth")] public ClaudeOAuth? ClaudeAiOauth { get; set; }
}

public enum ClaudeCredentialSourceKind
{
    File,
    KeychainCurrentUser,
    Environment
}

public sealed record ClaudeCredentialSource(ClaudeCredentialSourceKind Kind, string? Service = null)
{
    public string Label => Kind switch
    {
        ClaudeCredentialSourceKind.File => "file",
        ClaudeCredentialSourceKind.KeychainCurrentUser => "credentialStore",
        ClaudeCredentialSourceKind.Environment => "environment",
        _ => "unknown"
    };
}

public sealed class ClaudeCredentialState
{
    public required ClaudeOAuth OAuth { get; init; }
    public required ClaudeCredentialSource Source { get; init; }
    public ClaudeCredentialsFile? FullData { get; init; }
    public bool InferenceOnly { get; init; }

    public bool HasUsableAccessToken => !string.IsNullOrWhiteSpace(OAuth.AccessToken);

    public string DiagnosticsLabel(DateTimeOffset now)
    {
        var refresh = !string.IsNullOrEmpty(OAuth.RefreshToken) ? "yes" : "no";
        string expired;
        if (OAuth.ExpiresAt is { } expiresAt)
        {
            expired = expiresAt <= now.ToUnixTimeMilliseconds() ? "yes" : "no";
        }
        else
        {
            expired = "unknown";
        }
        return $"{Source.Label} refresh={refresh} expired={expired}";
    }
}

public enum ClaudeAuthErrorKind
{
    NotLoggedIn,
    SessionExpired,
    TokenExpired,
    CredentialsChanged,
    InvalidOAuthURL
}

public sealed class ClaudeAuthError : Exception, Models.ICategorizedError
{
    public ClaudeAuthErrorKind Kind { get; }

    public ClaudeAuthError(ClaudeAuthErrorKind kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    private static string Describe(ClaudeAuthErrorKind kind) => kind switch
    {
        ClaudeAuthErrorKind.NotLoggedIn => "Not logged in. Run `claude` to authenticate.",
        ClaudeAuthErrorKind.SessionExpired => "Session expired. Run `claude` to log in again.",
        ClaudeAuthErrorKind.TokenExpired => "Token expired. Run `claude` to log in again.",
        ClaudeAuthErrorKind.CredentialsChanged => "Claude login changed during refresh. Refresh again.",
        ClaudeAuthErrorKind.InvalidOAuthURL => "Invalid Claude OAuth URL. Check CLAUDE_CODE_CUSTOM_OAUTH_URL / CLAUDE_LOCAL_OAUTH_API_BASE.",
        _ => "Claude authentication error."
    };

    public bool AllowsAuthFallback => Kind is ClaudeAuthErrorKind.SessionExpired or ClaudeAuthErrorKind.TokenExpired;

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        ClaudeAuthErrorKind.NotLoggedIn => Models.ErrorCategory.NotLoggedIn,
        ClaudeAuthErrorKind.SessionExpired or ClaudeAuthErrorKind.TokenExpired => Models.ErrorCategory.AuthExpired,
        ClaudeAuthErrorKind.InvalidOAuthURL => Models.ErrorCategory.AuthInvalid,
        _ => Models.ErrorCategory.Other
    };
}

public sealed record ClaudeOAuthConfig(Uri UsageUrl, Uri RefreshUrl, string ClientId);

/// <summary>
/// Reads Claude Code's credentials on this machine. Windows port note: Claude Code (a cross-platform
/// npm CLI) writes `~/.claude/.credentials.json` (or `%CLAUDE_CONFIG_DIR%/.credentials.json`) on every
/// OS including Windows, so the file path is the primary and most reliable source here. Windows
/// Credential Manager is probed as a secondary source in case a future Claude Code version stores it
/// there (mirroring the macOS Keychain fallback), but the file is what real installs use today.
/// </summary>
public sealed class ClaudeAuthStore
{
    private const string DefaultClaudeHome = "~/.claude";
    private const string CredentialFileName = ".credentials.json";
    private const string ProdBaseApiUrl = "https://api.anthropic.com";
    private const string ProdRefreshUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ProdClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string NonProdClientId = "22422756-60c9-4084-8eb7-27705fd5cf9a";
    private const string KeychainServicePrefix = "Claude Code";

    private readonly IEnvironmentReading _environment;
    private readonly ITextFileAccessing _files;
    private readonly IKeychainAccessing _keychain;
    private readonly Func<DateTimeOffset> _now;

    public ClaudeAuthStore(
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

    public List<ClaudeCredentialState> LoadCredentialCandidates()
    {
        var candidates = new List<ClaudeCredentialState>();
        if (LoadFileCredentials() is { } file) candidates.Add(file);
        if (LoadKeychainCredentials() is { } keychain) candidates.Add(keychain);

        candidates = ApplyingEnvironmentToken(candidates);

        if (candidates.Count > 1)
        {
            AppLog.Debug(LogTag.AuthFor("claude"), $"credential candidates (file first): {string.Join(", ", candidates.Select(c => c.Source.Label))}");
        }
        else if (candidates.Count == 1)
        {
            AppLog.Debug(LogTag.AuthFor("claude"), $"credential source: {candidates[0].Source.Label}");
        }
        return candidates;
    }

    public bool HasCredentialFootprint()
    {
        if (_files.Exists(CredentialsPath())) return true;
        return KeychainServiceCandidates().Any(s => _keychain.GenericPasswordExists(s) == true);
    }

    private List<ClaudeCredentialState> ApplyingEnvironmentToken(List<ClaudeCredentialState> stored)
    {
        var envAccessToken = EnvText("CLAUDE_CODE_OAUTH_TOKEN");
        if (envAccessToken is null) return stored;

        var liveCapable = stored.Where(s => LiveUsageAvailability(s) == ClaudeLiveUsageAvailability.Available).ToList();
        var basis = liveCapable.FirstOrDefault() ?? stored.FirstOrDefault();
        var oauth = new ClaudeOAuth
        {
            AccessToken = envAccessToken,
            RefreshToken = basis?.OAuth.RefreshToken,
            ExpiresAt = basis?.OAuth.ExpiresAt,
            SubscriptionType = basis?.OAuth.SubscriptionType,
            RateLimitTier = basis?.OAuth.RateLimitTier,
            Scopes = basis?.OAuth.Scopes
        };
        var envCandidate = new ClaudeCredentialState
        {
            OAuth = oauth,
            Source = new ClaudeCredentialSource(ClaudeCredentialSourceKind.Environment),
            FullData = basis?.FullData,
            InferenceOnly = true
        };
        return liveCapable.Count == 0 ? new List<ClaudeCredentialState> { envCandidate } : liveCapable.Append(envCandidate).ToList();
    }

    public bool NeedsRefresh(ClaudeOAuth oauth)
    {
        if (oauth.ExpiresAt is not { } expiresAt) return false;
        return expiresAt - _now().ToUnixTimeMilliseconds() <= 5 * 60 * 1000;
    }

    public bool Save(ClaudeCredentialState state)
    {
        var fullData = state.FullData ?? new ClaudeCredentialsFile();
        fullData.ClaudeAiOauth = state.OAuth;
        var text = JsonSerializer.Serialize(fullData, new JsonSerializerOptions { WriteIndented = true });

        switch (state.Source.Kind)
        {
            case ClaudeCredentialSourceKind.File:
                _files.WriteText(CredentialsPath(), text);
                break;
            case ClaudeCredentialSourceKind.KeychainCurrentUser:
                _keychain.WriteGenericPassword(state.Source.Service!, text);
                break;
            default:
                return false;
        }
        AppLog.Debug(LogTag.AuthFor("claude"), $"persisted rotated credentials (source={state.Source.Label})");
        return true;
    }

    public const string UsageScope = "user:profile";

    public ClaudeLiveUsageAvailability LiveUsageAvailability(ClaudeCredentialState state)
    {
        if (state.InferenceOnly) return ClaudeLiveUsageAvailability.InferenceOnlyToken;
        if (state.OAuth.Scopes is null || state.OAuth.Scopes.Count == 0) return ClaudeLiveUsageAvailability.Available;
        return state.OAuth.Scopes.Contains(UsageScope) ? ClaudeLiveUsageAvailability.Available : ClaudeLiveUsageAvailability.MissingProfileScope;
    }

    private sealed record ResolvedOAuthEndpoints(string BaseApi, string RefreshUrl, string ClientId, string Suffix);

    private ResolvedOAuthEndpoints ResolveOAuthEndpoints()
    {
        string baseApi = ProdBaseApiUrl, refreshUrl = ProdRefreshUrl, clientId = ProdClientId, suffix = "";

        var isAntUser = EnvText("USER_TYPE") == "ant";
        if (isAntUser && EnvFlag("USE_LOCAL_OAUTH"))
        {
            var b = (EnvText("CLAUDE_LOCAL_OAUTH_API_BASE") ?? "http://localhost:8000").TrimmingTrailingSlashes();
            baseApi = b;
            refreshUrl = $"{b}/v1/oauth/token";
            clientId = NonProdClientId;
            suffix = "-local-oauth";
        }
        else if (isAntUser && EnvFlag("USE_STAGING_OAUTH"))
        {
            baseApi = "https://api-staging.anthropic.com";
            refreshUrl = "https://platform.staging.ant.dev/v1/oauth/token";
            clientId = NonProdClientId;
            suffix = "-staging-oauth";
        }

        var custom = EnvText("CLAUDE_CODE_CUSTOM_OAUTH_URL");
        if (custom is not null)
        {
            var b = custom.TrimmingTrailingSlashes();
            baseApi = b;
            refreshUrl = $"{b}/v1/oauth/token";
            suffix = "-custom-oauth";
        }
        var overrideClientId = EnvText("CLAUDE_CODE_OAUTH_CLIENT_ID");
        if (overrideClientId is not null) clientId = overrideClientId;

        return new ResolvedOAuthEndpoints(baseApi, refreshUrl, clientId, suffix);
    }

    public ClaudeOAuthConfig OAuthConfig()
    {
        var endpoints = ResolveOAuthEndpoints();
        var usageUrlString = $"{endpoints.BaseApi}/api/oauth/usage";
        if (!Uri.TryCreate(usageUrlString, UriKind.Absolute, out var usageUrl))
        {
            throw new ClaudeAuthError(ClaudeAuthErrorKind.InvalidOAuthURL);
        }
        if (!Uri.TryCreate(endpoints.RefreshUrl, UriKind.Absolute, out var refreshUrl))
        {
            throw new ClaudeAuthError(ClaudeAuthErrorKind.InvalidOAuthURL);
        }
        return new ClaudeOAuthConfig(usageUrl, refreshUrl, endpoints.ClientId);
    }

    public List<string> KeychainServiceCandidates()
    {
        var b = $"{KeychainServicePrefix}{ResolveOAuthEndpoints().Suffix}-credentials";
        return new List<string> { b };
    }

    public static ClaudeCredentialsFile? ParseCredentials(string text) =>
        ProviderParse.DecodeJsonWithHexFallback<ClaudeCredentialsFile>(text);

    private ClaudeCredentialState? LoadFileCredentials()
    {
        var path = CredentialsPath();
        if (!_files.Exists(path)) return null;
        string text;
        try { text = _files.ReadText(path); } catch { return null; }
        var parsed = ParseCredentials(text);
        if (parsed?.ClaudeAiOauth is not { } oauth || string.IsNullOrEmpty(oauth.AccessToken)) return null;
        return new ClaudeCredentialState { OAuth = oauth, Source = new ClaudeCredentialSource(ClaudeCredentialSourceKind.File), FullData = parsed };
    }

    private ClaudeCredentialState? LoadKeychainCredentials()
    {
        foreach (var service in KeychainServiceCandidates())
        {
            string? value;
            try { value = _keychain.ReadGenericPassword(service); } catch { value = null; }
            if (value is null) continue;
            var parsed = ParseCredentials(value);
            if (parsed?.ClaudeAiOauth is not { } oauth || string.IsNullOrEmpty(oauth.AccessToken)) continue;
            return new ClaudeCredentialState { OAuth = oauth, Source = new ClaudeCredentialSource(ClaudeCredentialSourceKind.KeychainCurrentUser, service), FullData = parsed };
        }
        return null;
    }

    private string CredentialsPath() => $"{EnvText("CLAUDE_CONFIG_DIR") ?? DefaultClaudeHome}/{CredentialFileName}";

    private string? EnvText(string name) => _environment.Value(name)?.Trim().NilIfEmpty();

    private bool EnvFlag(string name)
    {
        var value = EnvText(name)?.ToLowerInvariant();
        return value is not null && value != "0" && value != "false" && value != "no" && value != "off";
    }
}

public enum ClaudeLiveUsageAvailability
{
    Available,
    InferenceOnlyToken,
    MissingProfileScope
}
