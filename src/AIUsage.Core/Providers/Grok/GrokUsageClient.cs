using System.Text;
using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Grok;

public sealed record GrokRefreshResponse(string AccessToken, string? RefreshToken, string? IdToken, double? ExpiresIn);

public enum GrokUsageErrorKind
{
    ConnectionFailed,
    InvalidResponse,
    RequestFailed
}

public sealed class GrokUsageError : Exception, Models.ICategorizedError
{
    public GrokUsageErrorKind Kind { get; }
    public int? StatusCode { get; }

    public GrokUsageError(GrokUsageErrorKind kind, int? statusCode = null) : base(Describe(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Describe(GrokUsageErrorKind kind, int? statusCode) => kind switch
    {
        GrokUsageErrorKind.ConnectionFailed => "Grok billing request failed. Check your connection.",
        GrokUsageErrorKind.InvalidResponse => "Grok billing response changed.",
        GrokUsageErrorKind.RequestFailed => $"Grok billing request failed (HTTP {statusCode}). Try again later.",
        _ => "Grok usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        GrokUsageErrorKind.ConnectionFailed => Models.ErrorCategory.Network,
        GrokUsageErrorKind.InvalidResponse => Models.ErrorCategory.Decoding,
        GrokUsageErrorKind.RequestFailed => Models.ErrorCategoryExtensions.FromHttpStatus(StatusCode ?? 0),
        _ => Models.ErrorCategory.Other
    };
}

public sealed class GrokUsageClient
{
    public static readonly Uri SettingsUrl = new("https://cli-chat-proxy.grok.com/v1/settings");
    public static readonly Uri RefreshUrl = new("https://auth.x.ai/oauth2/token");
    public const string TokenAuthHeader = "xai-grok-cli";
    public static readonly Uri CreditsConfigUrl = new("https://cli-chat-proxy.grok.com/v1/billing?format=credits");

    private readonly IHttpClient _http;

    public GrokUsageClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    public async Task<HttpResponseResult> RefreshTokenAsync(string refreshToken, string clientId)
    {
        var body = $"grant_type=refresh_token&client_id={clientId.UrlFormEncoded()}&refresh_token={refreshToken.UrlFormEncoded()}";
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = RefreshUrl,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" },
            Body = Encoding.UTF8.GetBytes(body),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }

    public Task<HttpResponseResult> FetchCreditsConfigAsync(string accessToken) => GetAsync(CreditsConfigUrl, accessToken);
    public Task<HttpResponseResult> FetchSettingsAsync(string accessToken) => GetAsync(SettingsUrl, accessToken);

    public GrokRefreshResponse? DecodeRefreshResponse(HttpResponseResult response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String) return null;
            var accessToken = at.GetString();
            if (string.IsNullOrEmpty(accessToken)) return null;
            var refreshToken = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
            var idToken = root.TryGetProperty("id_token", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetDouble() : (double?)null;
            return new GrokRefreshResponse(accessToken, refreshToken, idToken, expiresIn);
        }
        catch
        {
            return null;
        }
    }

    private async Task<HttpResponseResult> GetAsync(Uri url, string accessToken)
    {
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = url,
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken.Trim()}",
                ["X-XAI-Token-Auth"] = TokenAuthHeader,
                ["Accept"] = "application/json",
                ["User-Agent"] = "AIUsage"
            },
            Timeout = TimeSpan.FromSeconds(10)
        }).ConfigureAwait(false);
    }
}
