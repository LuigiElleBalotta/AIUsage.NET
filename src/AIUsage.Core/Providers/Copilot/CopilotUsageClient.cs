using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.Copilot;

public enum CopilotUsageErrorKind
{
    InvalidResponse,
    ConnectionFailed,
    RequestFailed,
    QuotaUnavailable
}

public sealed class CopilotUsageError : Exception, Models.ICategorizedError
{
    public CopilotUsageErrorKind Kind { get; }
    public int? StatusCode { get; }

    public CopilotUsageError(CopilotUsageErrorKind kind, int? statusCode = null) : base(Describe(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Describe(CopilotUsageErrorKind kind, int? statusCode) => kind switch
    {
        CopilotUsageErrorKind.InvalidResponse => "Copilot usage response invalid. Try again later.",
        CopilotUsageErrorKind.ConnectionFailed => "Couldn't reach GitHub. Check your connection.",
        CopilotUsageErrorKind.RequestFailed => $"Copilot usage request failed (HTTP {statusCode}). Try again later.",
        CopilotUsageErrorKind.QuotaUnavailable => "Copilot usage data is unavailable for this account.",
        _ => "Copilot usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        CopilotUsageErrorKind.InvalidResponse => Models.ErrorCategory.Decoding,
        CopilotUsageErrorKind.ConnectionFailed => Models.ErrorCategory.Network,
        CopilotUsageErrorKind.RequestFailed => Models.ErrorCategoryExtensions.FromHttpStatus(StatusCode ?? 0),
        CopilotUsageErrorKind.QuotaUnavailable => Models.ErrorCategory.NotAvailable,
        _ => Models.ErrorCategory.Other
    };
}

public sealed class CopilotUsageClient
{
    public static readonly Uri UsageUrl = new("https://api.github.com/copilot_internal/user");

    private readonly IHttpClient _http;

    public CopilotUsageClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    public async Task<HttpResponseResult> FetchUsageAsync(string token)
    {
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = UsageUrl,
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"token {token}",
                ["Accept"] = "application/json",
                ["Editor-Version"] = "vscode/1.96.2",
                ["Editor-Plugin-Version"] = "copilot-chat/0.26.7",
                ["User-Agent"] = "GitHubCopilotChat/0.26.7",
                ["X-Github-Api-Version"] = "2025-04-01"
            },
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }
}
