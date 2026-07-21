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
/// Which login a <see cref="ClaudeAuthStore"/> is allowed to see. <see cref="Standard"/> is the
/// default card — byte-identical to the store's historical behavior. <see cref="ConfigDir"/> backs an
/// extra account card and deliberately has no cross-account, environment-token, or Desktop fallback:
/// the card can only ever read the one login it was created for. Direct port of the Swift
/// ClaudeCredentialScope.
/// </summary>
public abstract record ClaudeCredentialScope
{
    public sealed record Standard : ClaudeCredentialScope;

    /// <summary>One extra CLAUDE_CONFIG_DIR home. <paramref name="KeychainLiteral"/> is the literal
    /// string whose hash names the Credential Manager item (Claude Code hashes the env value as
    /// typed — "~/…" vs absolute differ).</summary>
    public sealed record ConfigDir(string Path, string KeychainLiteral) : ClaudeCredentialScope;

    public static readonly ClaudeCredentialScope StandardScope = new Standard();
}

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

    public ClaudeCredentialScope Scope { get; }
    /// <summary>Whether the Standard store may fall back to Claude Desktop's credentials. On by
    /// default (the historical behavior); the catalog turns it OFF once extra Claude account cards
    /// exist, because the Desktop login could belong to any of them — borrowing it unpinned could
    /// fetch one account's usage onto another account's card.</summary>
    public bool AllowsDesktopFallback { get; }

    public ClaudeAuthStore(
        IEnvironmentReading? environment = null,
        ITextFileAccessing? files = null,
        IKeychainAccessing? keychain = null,
        ClaudeCredentialScope? scope = null,
        bool allowsDesktopFallback = true,
        Func<DateTimeOffset>? now = null)
    {
        _environment = environment ?? new ProcessEnvironmentReader();
        _files = files ?? new LocalTextFileAccessor();
        _keychain = keychain ?? new WindowsCredentialAccessor();
        Scope = scope ?? ClaudeCredentialScope.StandardScope;
        AllowsDesktopFallback = allowsDesktopFallback;
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

    /// <summary>Whether this scoped card's login leaves any local footprint, checked without ever
    /// reading a keychain secret — safe for the every-launch seeding probe (NewProviderSeeder), which
    /// must never raise a permission dialog.</summary>
    public bool HasCredentialFootprint()
    {
        if (_files.Exists(CredentialsPath())) return true;
        return KeychainServiceCandidates().Any(s => _keychain.GenericPasswordExists(s) == true);
    }

    private List<ClaudeCredentialState> ApplyingEnvironmentToken(List<ClaudeCredentialState> stored)
    {
        // An ambient env token describes the DEFAULT login's environment; a scoped card must never
        // inherit it (that would leak one account's token into another account's card).
        if (Scope is not ClaudeCredentialScope.Standard) return stored;

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

    /// <summary>The Credential Manager service names as this environment's Claude Code writes
    /// them — the single source both the scoped store and config-dir discovery build from, so a
    /// non-prod OAuth setup (local/staging/custom, which suffixes the service) can never make
    /// discovery probe one name while refresh reads another.</summary>
    public static string BaseKeychainServiceName(IEnvironmentReading environment)
    {
        var suffix = new ClaudeAuthStore(environment: environment).ResolveOAuthEndpoints().Suffix;
        return $"{KeychainServicePrefix}{suffix}-credentials";
    }

    public static string ScopedKeychainServiceName(string configDirLiteral, IEnvironmentReading environment) =>
        $"{BaseKeychainServiceName(environment)}-{HashSuffix(configDirLiteral)}";

    public List<string> KeychainServiceCandidates()
    {
        // Only needs the file suffix, which never fails — keep this off the throwing URL path so
        // credential loading stays forgiving even when a custom OAuth URL is malformed.
        var b = $"{KeychainServicePrefix}{ResolveOAuthEndpoints().Suffix}-credentials";
        switch (Scope)
        {
            case ClaudeCredentialScope.ConfigDir configDir:
                // Exactly this card's item — never the bare default service, which is another
                // account's login.
                return new List<string> { $"{b}-{HashSuffix(configDir.KeychainLiteral)}" };
            case ClaudeCredentialScope.Standard:
            default:
                var configDirOverride = EnvText("CLAUDE_CONFIG_DIR");
                if (configDirOverride is not null)
                {
                    return new List<string> { $"{b}-{HashSuffix(configDirOverride)}", b };
                }
                return new List<string> { b };
        }
    }

    /// <summary>Direct port of Swift's private hashSuffix: SHA-256 of the NFC-normalized literal,
    /// hex-encoded, first 8 characters. Must stay byte-identical to discovery's computation
    /// (<see cref="ScopedKeychainServiceName"/>) or a scoped card could probe one service name while
    /// Claude Code itself writes to another.</summary>
    private static string HashSuffix(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormC);
        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest).ToLowerInvariant()[..8];
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

    private string CredentialsPath()
    {
        if (Scope is ClaudeCredentialScope.ConfigDir configDir) return $"{configDir.Path}/{CredentialFileName}";
        return $"{EnvText("CLAUDE_CONFIG_DIR") ?? DefaultClaudeHome}/{CredentialFileName}";
    }

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
