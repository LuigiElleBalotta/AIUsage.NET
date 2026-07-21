using System.Text;
using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Antigravity;

public abstract record CloudCodeOutcome
{
    public sealed record Ok(byte[] Data) : CloudCodeOutcome;
    public sealed record AuthFailed : CloudCodeOutcome;
    public sealed record Unavailable : CloudCodeOutcome;
}

public abstract record TokenRefreshOutcome
{
    public sealed record Refreshed(string AccessToken, double ExpiresIn) : TokenRefreshOutcome;
    public sealed record AuthFailed : TokenRefreshOutcome;
    public sealed record Unavailable : TokenRefreshOutcome;
}

/// <summary>All network I/O for Antigravity. Direct port of AntigravityUsageClient.</summary>
public sealed class AntigravityUsageClient
{
    public const string LsService = "exa.language_server_pb.LanguageServerService";
    public static readonly string[] CloudCodeUrls = { "https://daily-cloudcode-pa.googleapis.com", "https://cloudcode-pa.googleapis.com" };
    public const string FetchModelsPath = "/v1internal:fetchAvailableModels";
    public const string LoadCodeAssistPath = "/v1internal:loadCodeAssist";
    public const string RetrieveQuotaPath = "/v1internal:retrieveUserQuota";
    public const string QuotaSummaryPath = "/v1internal:retrieveUserQuotaSummary";
    public static readonly Uri GoogleOAuthUrl = new("https://oauth2.googleapis.com/token");
    public const string GoogleClientId = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    public const string GoogleClientSecret = "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";
    public static readonly Dictionary<string, string> LsMetadata = new()
    {
        ["ideName"] = "antigravity", ["extensionName"] = "antigravity", ["ideVersion"] = "unknown", ["locale"] = "en"
    };

    private readonly IHttpClient _lsHttp;
    private readonly IHttpClient _http;

    public AntigravityUsageClient(IHttpClient? lsHttp = null, IHttpClient? http = null)
    {
        _lsHttp = lsHttp ?? new SystemHttpClient();
        _http = http ?? new SystemHttpClient();
    }

    public async Task<HttpResponseResult?> CallLsAsync(string scheme, int port, string csrf, string method)
    {
        if (!Uri.TryCreate($"{scheme}://127.0.0.1:{port}/{LsService}/{method}", UriKind.Absolute, out var url)) return null;
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["metadata"] = LsMetadata });
        try
        {
            return await _lsHttp.SendAsync(new HttpRequestSpec
            {
                Method = "POST",
                Url = url,
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["Connect-Protocol-Version"] = "1", ["x-codeium-csrf-token"] = csrf },
                Body = body,
                Timeout = TimeSpan.FromSeconds(10)
            }).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<CloudCodeOutcome> CloudCodeAsync(string path, string token, string userAgent, Dictionary<string, string> body)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(body);
        foreach (var basev in CloudCodeUrls)
        {
            if (!Uri.TryCreate(basev + path, UriKind.Absolute, out var url)) continue;
            HttpResponseResult response;
            try
            {
                response = await _http.SendAsync(new HttpRequestSpec
                {
                    Method = "POST",
                    Url = url,
                    Headers = new Dictionary<string, string>
                    {
                        ["Accept"] = "application/json",
                        ["Content-Type"] = "application/json",
                        ["Authorization"] = $"Bearer {token}",
                        ["User-Agent"] = userAgent
                    },
                    Body = payload,
                    Timeout = TimeSpan.FromSeconds(15)
                }).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }
            if (response.StatusCode is 401 or 403) return new CloudCodeOutcome.AuthFailed();
            if (response.StatusCode is >= 200 and < 300) return new CloudCodeOutcome.Ok(response.Body);
        }
        return new CloudCodeOutcome.Unavailable();
    }

    public async Task<TokenRefreshOutcome> RefreshGoogleTokenAsync(string refreshToken)
    {
        var form = string.Join("&", new[]
        {
            $"client_id={GoogleClientId.UrlFormEncoded()}",
            $"client_secret={GoogleClientSecret.UrlFormEncoded()}",
            $"refresh_token={refreshToken.UrlFormEncoded()}",
            "grant_type=refresh_token"
        });
        HttpResponseResult response;
        try
        {
            response = await _http.SendAsync(new HttpRequestSpec
            {
                Method = "POST",
                Url = GoogleOAuthUrl,
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" },
                Body = Encoding.UTF8.GetBytes(form),
                Timeout = TimeSpan.FromSeconds(15)
            }).ConfigureAwait(false);
        }
        catch
        {
            return new TokenRefreshOutcome.Unavailable();
        }

        switch (response.StatusCode)
        {
            case >= 200 and < 300:
                try
                {
                    using var doc = JsonDocument.Parse(response.Body);
                    var access = doc.RootElement.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString()?.Trim().NilIfEmpty() : null;
                    if (access is null) return new TokenRefreshOutcome.Unavailable();
                    var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetDouble() : 3600;
                    return new TokenRefreshOutcome.Refreshed(access, expiresIn);
                }
                catch
                {
                    return new TokenRefreshOutcome.Unavailable();
                }
            case 408 or 429:
                return new TokenRefreshOutcome.Unavailable();
            case >= 400 and < 500:
                return new TokenRefreshOutcome.AuthFailed();
            default:
                return new TokenRefreshOutcome.Unavailable();
        }
    }
}
