using System.Text;
using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Codex;

public sealed record CodexRefreshResponse(string AccessToken, string? RefreshToken, string? IdToken);

public enum CodexUsageErrorKind
{
    RequestFailed,
    InvalidResponse,
    ConnectionFailed
}

public sealed class CodexUsageError : Exception, Models.ICategorizedError
{
    public CodexUsageErrorKind Kind { get; }
    public int? StatusCode { get; }

    public CodexUsageError(CodexUsageErrorKind kind, int? statusCode = null) : base(Describe(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Describe(CodexUsageErrorKind kind, int? statusCode) => kind switch
    {
        CodexUsageErrorKind.RequestFailed => ProviderUsageErrorText.RequestFailed(statusCode ?? 0),
        CodexUsageErrorKind.InvalidResponse => ProviderUsageErrorText.InvalidResponse,
        CodexUsageErrorKind.ConnectionFailed => ProviderUsageErrorText.ConnectionFailed,
        _ => "Codex usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        CodexUsageErrorKind.RequestFailed => Models.ErrorCategoryExtensions.FromHttpStatus(StatusCode ?? 0),
        CodexUsageErrorKind.InvalidResponse => Models.ErrorCategory.Decoding,
        CodexUsageErrorKind.ConnectionFailed => Models.ErrorCategory.Network,
        _ => Models.ErrorCategory.Other
    };
}

public sealed class CodexUsageClient
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public static readonly Uri RefreshUrl = new("https://auth.openai.com/oauth/token");
    public static readonly Uri UsageUrl = new("https://chatgpt.com/backend-api/wham/usage");
    public static readonly Uri ResetCreditsUrl = new("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
    public static readonly Uri ConsumeResetCreditUrl = new("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume");

    private readonly IHttpClient _http;

    public CodexUsageClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    public async Task<CodexRefreshResponse> RefreshTokenAsync(string refreshToken)
    {
        var body = $"grant_type=refresh_token&client_id={ClientId.UrlFormEncoded()}&refresh_token={refreshToken.UrlFormEncoded()}";
        var response = await _http.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = RefreshUrl,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" },
            Body = Encoding.UTF8.GetBytes(body),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);

        if (response.StatusCode is 400 or 401)
        {
            var errorBody = ProviderParse.JsonObject(response.Body);
            string? code = null;
            if (errorBody is { } b)
            {
                if (b.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.Object)
                    {
                        if (e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                        else if (e.TryGetProperty("error", out var c2) && c2.ValueKind == JsonValueKind.String) code = c2.GetString();
                    }
                    else if (e.ValueKind == JsonValueKind.String)
                    {
                        code = e.GetString();
                    }
                }
                code ??= b.TryGetProperty("code", out var cc) && cc.ValueKind == JsonValueKind.String ? cc.GetString() : null;
            }

            switch (code)
            {
                case "refresh_token_expired": throw new CodexAuthError(CodexAuthErrorKind.SessionExpired);
                case "refresh_token_reused": throw new CodexAuthError(CodexAuthErrorKind.TokenConflict);
                case "refresh_token_invalidated": throw new CodexAuthError(CodexAuthErrorKind.TokenRevoked);
                default: throw new CodexUsageError(CodexUsageErrorKind.RequestFailed, response.StatusCode);
            }
        }

        if (response.StatusCode is < 200 or >= 300) throw new CodexUsageError(CodexUsageErrorKind.RequestFailed, response.StatusCode);

        var body2 = ProviderParse.JsonObject(response.Body);
        if (body2 is not { } root || !root.TryGetProperty("access_token", out var atEl) || atEl.ValueKind != JsonValueKind.String)
        {
            throw new CodexAuthError(CodexAuthErrorKind.TokenExpired);
        }
        var accessToken = atEl.GetString();
        if (string.IsNullOrEmpty(accessToken)) throw new CodexAuthError(CodexAuthErrorKind.TokenExpired);

        string? refreshTokenOut = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
        string? idToken = root.TryGetProperty("id_token", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : null;
        return new CodexRefreshResponse(accessToken, refreshTokenOut, idToken);
    }

    public async Task<HttpResponseResult> FetchUsageAsync(string accessToken, string? accountId)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["Accept"] = "application/json",
            ["User-Agent"] = "AIUsage"
        };
        if (!string.IsNullOrEmpty(accountId)) headers["ChatGPT-Account-Id"] = accountId;

        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = UsageUrl,
            Headers = headers,
            Timeout = TimeSpan.FromSeconds(10)
        }).ConfigureAwait(false);
    }

    public async Task<HttpResponseResult> FetchResetCreditsAsync(string accessToken, string? accountId)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["Accept"] = "application/json",
            ["User-Agent"] = "AIUsage",
            ["OpenAI-Beta"] = "codex-1",
            ["originator"] = "Codex Desktop"
        };
        if (!string.IsNullOrEmpty(accountId)) headers["ChatGPT-Account-Id"] = accountId;

        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = ResetCreditsUrl,
            Headers = headers,
            Timeout = TimeSpan.FromSeconds(10)
        }).ConfigureAwait(false);
    }

    public async Task<HttpResponseResult> ConsumeResetCreditAsync(string accessToken, string? accountId, string creditId, string redeemRequestId)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json",
            ["User-Agent"] = "AIUsage",
            ["OpenAI-Beta"] = "codex-1",
            ["originator"] = "Codex Desktop"
        };
        if (!string.IsNullOrEmpty(accountId)) headers["ChatGPT-Account-Id"] = accountId;

        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["redeem_request_id"] = redeemRequestId, ["credit_id"] = creditId });
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = ConsumeResetCreditUrl,
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(payload),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }
}
