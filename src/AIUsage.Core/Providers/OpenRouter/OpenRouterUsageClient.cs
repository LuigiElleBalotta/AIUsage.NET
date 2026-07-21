using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.OpenRouter;

public enum OpenRouterUsageErrorKind
{
    ConnectionFailed,
    InvalidResponse,
    RequestFailed
}

public sealed class OpenRouterUsageError : Exception, Models.ICategorizedError
{
    public OpenRouterUsageErrorKind Kind { get; }
    public int? StatusCode { get; }

    public OpenRouterUsageError(OpenRouterUsageErrorKind kind, int? statusCode = null) : base(Describe(kind, statusCode))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    private static string Describe(OpenRouterUsageErrorKind kind, int? statusCode) => kind switch
    {
        OpenRouterUsageErrorKind.ConnectionFailed => "Couldn't reach OpenRouter. Check your connection.",
        OpenRouterUsageErrorKind.InvalidResponse => "OpenRouter usage data unavailable. Try again later.",
        OpenRouterUsageErrorKind.RequestFailed => $"OpenRouter request failed (HTTP {statusCode}).",
        _ => "OpenRouter usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        OpenRouterUsageErrorKind.ConnectionFailed => Models.ErrorCategory.Network,
        OpenRouterUsageErrorKind.InvalidResponse => Models.ErrorCategory.Decoding,
        OpenRouterUsageErrorKind.RequestFailed => Models.ErrorCategoryExtensions.FromHttpStatus(StatusCode ?? 0),
        _ => Models.ErrorCategory.Other
    };
}

public sealed class OpenRouterUsageClient
{
    public static readonly Uri CreditsUrl = new("https://openrouter.ai/api/v1/credits");
    public static readonly Uri KeyUrl = new("https://openrouter.ai/api/v1/key");

    private readonly IHttpClient _http;

    public OpenRouterUsageClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    public Task<HttpResponseResult> FetchCreditsAsync(string apiKey) => GetAsync(CreditsUrl, apiKey);
    public Task<HttpResponseResult> FetchKeyAsync(string apiKey) => GetAsync(KeyUrl, apiKey);

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
