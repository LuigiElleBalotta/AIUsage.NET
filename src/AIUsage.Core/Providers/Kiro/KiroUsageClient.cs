using System.Text;
using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Kiro;

public sealed record KiroRefreshResponse(string AccessToken, string? RefreshToken, string? ProfileArn);

public enum KiroUsageErrorKind
{
    RequestFailed,
    InvalidResponse,
    ConnectionFailed
}

public sealed class KiroUsageError : Exception, Models.ICategorizedError
{
    public KiroUsageErrorKind Kind { get; }
    public int? StatusCode { get; }

    public KiroUsageError(KiroUsageErrorKind kind, int? statusCode = null) : base(Describe(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Describe(KiroUsageErrorKind kind, int? statusCode) => kind switch
    {
        KiroUsageErrorKind.RequestFailed => ProviderUsageErrorText.RequestFailed(statusCode ?? 0),
        KiroUsageErrorKind.InvalidResponse => ProviderUsageErrorText.InvalidResponse,
        KiroUsageErrorKind.ConnectionFailed => ProviderUsageErrorText.ConnectionFailed,
        _ => "Kiro usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        KiroUsageErrorKind.RequestFailed => Models.ErrorCategoryExtensions.FromHttpStatus(StatusCode ?? 0),
        KiroUsageErrorKind.InvalidResponse => Models.ErrorCategory.Decoding,
        KiroUsageErrorKind.ConnectionFailed => Models.ErrorCategory.Network,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>
/// Calls Kiro's CodeWhisperer/Q Developer backend. Reverse-engineered, undocumented API — endpoints
/// and payload shapes may change without notice. us-east-1 is served by the CodeWhisperer REST host;
/// every other region only exists on the regional Amazon Q host (both expose the same operations).
/// </summary>
public sealed class KiroUsageClient
{
    private const string CodeWhispererHost = "codewhisperer.us-east-1.amazonaws.com";
    private const string UserAgent = "AIUsage";

    private readonly IHttpClient _http;

    public KiroUsageClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    private static string ApiBase(string region) => region == "us-east-1"
        ? $"https://{CodeWhispererHost}"
        : $"https://q.{region}.amazonaws.com";

    public async Task<HttpResponseResult> FetchUsageLimitsAsync(string accessToken, string region, string? profileArn)
    {
        var url = $"{ApiBase(region)}/getUsageLimits?origin=AI_EDITOR&resourceType=AGENTIC_REQUEST&isEmailRequired=true";
        if (!string.IsNullOrEmpty(profileArn)) url += $"&profileArn={Uri.EscapeDataString(profileArn)}";

        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = new Uri(url),
            Headers = BaseHeaders(accessToken),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }

    public async Task<HttpResponseResult> ListAvailableProfilesAsync(string accessToken, string region)
    {
        var url = $"{ApiBase(region)}/ListAvailableProfiles";
        var headers = BaseHeaders(accessToken);
        headers["Content-Type"] = "application/json";

        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = new Uri(url),
            Headers = headers,
            Body = Encoding.UTF8.GetBytes("""{"maxResults":10}"""),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }

    /// <summary>Refresh via Kiro's own desktop-auth endpoint (Kiro IDE logins) — a bare refresh
    /// token, no client secret.</summary>
    public async Task<KiroRefreshResponse> RefreshDesktopTokenAsync(string refreshToken, string region)
    {
        var url = $"https://prod.{region}.auth.desktop.kiro.dev/refreshToken";
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["refreshToken"] = refreshToken });

        var response = await _http.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = new Uri(url),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["User-Agent"] = UserAgent },
            Body = Encoding.UTF8.GetBytes(payload),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);

        return ParseRefreshResponse(response);
    }

    /// <summary>Refresh via AWS SSO OIDC (`kiro-cli` logins) — needs the paired clientId/clientSecret
    /// from the CLI's device-registration record.</summary>
    public async Task<KiroRefreshResponse> RefreshCliTokenAsync(string refreshToken, string clientId, string clientSecret, string region)
    {
        var url = $"https://oidc.{region}.amazonaws.com/token";
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grantType"] = "refresh_token",
            ["clientId"] = clientId,
            ["clientSecret"] = clientSecret,
            ["refreshToken"] = refreshToken
        });

        var response = await _http.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = new Uri(url),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["User-Agent"] = UserAgent },
            Body = Encoding.UTF8.GetBytes(payload),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);

        return ParseRefreshResponse(response);
    }

    private static KiroRefreshResponse ParseRefreshResponse(HttpResponseResult response)
    {
        if (response.StatusCode is < 200 or >= 300) throw new KiroAuthError(KiroAuthErrorKind.SessionExpired);

        var body = ProviderParse.JsonObject(response.Body);
        if (body is not { } root || !root.TryGetProperty("accessToken", out var atEl) || atEl.ValueKind != JsonValueKind.String)
        {
            throw new KiroAuthError(KiroAuthErrorKind.InvalidAuthPayload);
        }
        var accessToken = atEl.GetString();
        if (string.IsNullOrEmpty(accessToken)) throw new KiroAuthError(KiroAuthErrorKind.InvalidAuthPayload);

        var refreshToken = root.TryGetProperty("refreshToken", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
        var profileArn = root.TryGetProperty("profileArn", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() : null;
        return new KiroRefreshResponse(accessToken!, refreshToken, profileArn);
    }

    private static Dictionary<string, string> BaseHeaders(string accessToken) => new()
    {
        ["Authorization"] = $"Bearer {accessToken}",
        ["Accept"] = "application/json",
        ["User-Agent"] = UserAgent,
        ["x-amzn-codewhisperer-optout"] = "true"
    };
}
