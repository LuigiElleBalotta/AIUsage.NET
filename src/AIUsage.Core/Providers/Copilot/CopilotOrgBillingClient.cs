using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.Copilot;

/// <summary>Calls GitHub's org billing REST endpoints. Direct port of CopilotOrgBillingClient.</summary>
public sealed class CopilotOrgBillingClient
{
    public static readonly Uri UserOrgsUrl = new("https://api.github.com/user/orgs?per_page=100");

    public static Uri? UsageSummaryUrl(string org)
    {
        var encoded = Uri.EscapeDataString(org);
        return Uri.TryCreate($"https://api.github.com/orgs/{encoded}/settings/billing/usage/summary", UriKind.Absolute, out var uri) ? uri : null;
    }

    private readonly IHttpClient _http;

    public CopilotOrgBillingClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    public Task<HttpResponseResult> FetchUserOrgsAsync(string token) => SendAsync(UserOrgsUrl, token);

    public Task<HttpResponseResult> FetchUsageSummaryAsync(string org, string token)
    {
        var url = UsageSummaryUrl(org) ?? throw new CopilotUsageError(CopilotUsageErrorKind.InvalidResponse);
        return SendAsync(url, token);
    }

    private async Task<HttpResponseResult> SendAsync(Uri url, string token)
    {
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = url,
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"token {token}",
                ["Accept"] = "application/vnd.github+json",
                ["User-Agent"] = "AIUsage",
                ["X-GitHub-Api-Version"] = "2022-11-28"
            },
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }
}
