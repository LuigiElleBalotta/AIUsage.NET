using System.Text;
using System.Text.Json;
using AIUsage.Core.Providers;
using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.Claude;

public sealed class ClaudeRefreshResponse
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public double? ExpiresIn { get; set; }
}

public enum ClaudeUsageErrorKind
{
    ConnectionFailed,
    InvalidResponse,
    RequestFailed
}

public sealed class ClaudeUsageError : Exception, Models.ICategorizedError
{
    public ClaudeUsageErrorKind Kind { get; }
    public int? StatusCode { get; }

    public ClaudeUsageError(ClaudeUsageErrorKind kind, int? statusCode = null) : base(Describe(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Describe(ClaudeUsageErrorKind kind, int? statusCode) => kind switch
    {
        ClaudeUsageErrorKind.ConnectionFailed => ProviderUsageErrorText.ConnectionFailed,
        ClaudeUsageErrorKind.InvalidResponse => ProviderUsageErrorText.InvalidResponse,
        ClaudeUsageErrorKind.RequestFailed => ProviderUsageErrorText.RequestFailed(statusCode ?? 0),
        _ => "Claude usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        ClaudeUsageErrorKind.ConnectionFailed => Models.ErrorCategory.Network,
        ClaudeUsageErrorKind.InvalidResponse => Models.ErrorCategory.Decoding,
        ClaudeUsageErrorKind.RequestFailed => Models.ErrorCategoryExtensions.FromHttpStatus(StatusCode ?? 0),
        _ => Models.ErrorCategory.Other
    };
}

public sealed class ClaudeUsageClient
{
    private const string Scopes = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    private readonly IHttpClient _httpClient;

    public ClaudeUsageClient(IHttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new SystemHttpClient();
    }

    public async Task<HttpResponseResult> RefreshTokenAsync(string refreshToken, ClaudeOAuthConfig config)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = config.ClientId,
            ["scope"] = Scopes
        });
        return await _httpClient.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = config.RefreshUrl,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body = Encoding.UTF8.GetBytes(body),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }

    public async Task<HttpResponseResult> FetchUsageAsync(string accessToken, ClaudeOAuthConfig config)
    {
        return await _httpClient.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = config.UsageUrl,
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken.Trim()}",
                ["Accept"] = "application/json",
                ["Content-Type"] = "application/json",
                ["anthropic-beta"] = "oauth-2025-04-20",
                ["User-Agent"] = "claude-code/2.1.69"
            },
            Timeout = TimeSpan.FromSeconds(10)
        }).ConfigureAwait(false);
    }
}
