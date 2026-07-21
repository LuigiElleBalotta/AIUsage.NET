using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.ZAI;

public enum ZAIUsageErrorKind
{
    ConnectionFailed,
    InvalidResponse,
    RequestFailed,
    NoCodingPlan
}

public sealed class ZAIUsageError : Exception, Models.ICategorizedError
{
    public ZAIUsageErrorKind Kind { get; }
    public int? StatusCode { get; }

    public ZAIUsageError(ZAIUsageErrorKind kind, int? statusCode = null) : base(Describe(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Describe(ZAIUsageErrorKind kind, int? statusCode) => kind switch
    {
        ZAIUsageErrorKind.ConnectionFailed => Providers.ProviderUsageErrorText.ConnectionFailed,
        ZAIUsageErrorKind.InvalidResponse => Providers.ProviderUsageErrorText.InvalidResponse,
        ZAIUsageErrorKind.RequestFailed => Providers.ProviderUsageErrorText.RequestFailed(statusCode ?? 0),
        ZAIUsageErrorKind.NoCodingPlan => "No active GLM Coding Plan. Subscribe at z.ai/subscribe to see usage.",
        _ => "Z.ai usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        ZAIUsageErrorKind.ConnectionFailed => Models.ErrorCategory.Network,
        ZAIUsageErrorKind.InvalidResponse => Models.ErrorCategory.Decoding,
        ZAIUsageErrorKind.RequestFailed => Models.ErrorCategoryExtensions.FromHttpStatus(StatusCode ?? 0),
        ZAIUsageErrorKind.NoCodingPlan => Models.ErrorCategory.NotAvailable,
        _ => Models.ErrorCategory.Other
    };
}

public sealed class ZAIUsageClient
{
    public static readonly Uri SubscriptionUrl = new("https://api.z.ai/api/biz/subscription/list");
    public static readonly Uri QuotaUrl = new("https://api.z.ai/api/monitor/usage/quota/limit");

    private readonly IHttpClient _http;

    public ZAIUsageClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    public Task<HttpResponseResult> FetchSubscriptionAsync(string apiKey) => GetAsync(SubscriptionUrl, apiKey);
    public Task<HttpResponseResult> FetchQuotaAsync(string apiKey) => GetAsync(QuotaUrl, apiKey);

    private async Task<HttpResponseResult> GetAsync(Uri url, string apiKey)
    {
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = url,
            Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}", ["Accept"] = "application/json" },
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }
}
