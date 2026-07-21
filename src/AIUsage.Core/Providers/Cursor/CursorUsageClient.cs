using System.Text;
using System.Text.Json;
using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.Cursor;

public sealed record CursorSession(string UserId, string SessionToken);

public sealed class CursorUsageClient
{
    public static readonly Uri UsageUrl = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage");
    public static readonly Uri PlanUrl = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetPlanInfo");
    public static readonly Uri RefreshUrl = new("https://api2.cursor.sh/oauth/token");
    public static readonly Uri CreditsUrl = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetCreditGrantsBalance");
    public static readonly Uri RestUsageUrl = new("https://cursor.com/api/usage");
    public static readonly Uri UsageSummaryUrl = new("https://cursor.com/api/usage-summary");
    public static readonly Uri StripeUrl = new("https://cursor.com/api/auth/stripe");
    public static readonly Uri ExportCsvUrl = new("https://cursor.com/api/dashboard/export-usage-events-csv");
    public const string ClientId = "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB";

    private readonly IHttpClient _http;

    public CursorUsageClient(IHttpClient? http = null)
    {
        _http = http ?? new SystemHttpClient();
    }

    public async Task<HttpResponseResult> RefreshTokenAsync(string refreshToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken
        });
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = RefreshUrl,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body = Encoding.UTF8.GetBytes(body),
            Timeout = TimeSpan.FromSeconds(15)
        }).ConfigureAwait(false);
    }

    public Task<HttpResponseResult> FetchUsageAsync(string accessToken) => ConnectPostAsync(UsageUrl, accessToken);
    public Task<HttpResponseResult> FetchPlanAsync(string accessToken) => ConnectPostAsync(PlanUrl, accessToken);
    public Task<HttpResponseResult> FetchCreditsAsync(string accessToken) => ConnectPostAsync(CreditsUrl, accessToken);

    public async Task<HttpResponseResult?> FetchRequestBasedUsageAsync(string accessToken)
    {
        var session = SessionFrom(accessToken);
        if (session is null) return null;
        var url = new Uri($"{RestUsageUrl}?user={Uri.EscapeDataString(session.UserId)}");
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = url,
            Headers = new Dictionary<string, string> { ["Cookie"] = $"WorkosCursorSessionToken={session.SessionToken}" },
            Timeout = TimeSpan.FromSeconds(10)
        }).ConfigureAwait(false);
    }

    public async Task<HttpResponseResult?> FetchUsageSummaryAsync(string accessToken)
    {
        var session = SessionFrom(accessToken);
        if (session is null) return null;
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = UsageSummaryUrl,
            Headers = new Dictionary<string, string> { ["Cookie"] = $"WorkosCursorSessionToken={session.SessionToken}" },
            Timeout = TimeSpan.FromSeconds(10)
        }).ConfigureAwait(false);
    }

    public async Task<HttpResponseResult?> FetchStripeBalanceAsync(string accessToken)
    {
        var session = SessionFrom(accessToken);
        if (session is null) return null;
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = StripeUrl,
            Headers = new Dictionary<string, string> { ["Cookie"] = $"WorkosCursorSessionToken={session.SessionToken}" },
            Timeout = TimeSpan.FromSeconds(10)
        }).ConfigureAwait(false);
    }

    public async Task<HttpResponseResult?> FetchUsageCsvAsync(string accessToken, DateTimeOffset start, DateTimeOffset end)
    {
        var session = SessionFrom(accessToken);
        if (session is null) return null;
        var url = new Uri($"{ExportCsvUrl}?startDate={start.ToUnixTimeMilliseconds()}&endDate={end.ToUnixTimeMilliseconds()}&strategy=tokens");
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "GET",
            Url = url,
            Headers = new Dictionary<string, string> { ["Cookie"] = $"WorkosCursorSessionToken={session.SessionToken}", ["Accept"] = "text/csv" },
            Timeout = TimeSpan.FromSeconds(30)
        }).ConfigureAwait(false);
    }

    private async Task<HttpResponseResult> ConnectPostAsync(Uri url, string accessToken)
    {
        return await _http.SendAsync(new HttpRequestSpec
        {
            Method = "POST",
            Url = url,
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken}",
                ["Content-Type"] = "application/json",
                ["Connect-Protocol-Version"] = "1"
            },
            Body = Encoding.UTF8.GetBytes("{}"),
            Timeout = TimeSpan.FromSeconds(10)
        }).ConfigureAwait(false);
    }

    public static CursorSession? SessionFrom(string accessToken)
    {
        var subject = CursorAuthStore.TokenSubject(accessToken);
        if (subject is null) return null;
        var parts = subject.Split('|');
        var userId = parts.Length > 1 ? parts[1] : parts[0];
        if (userId.Length == 0) return null;
        return new CursorSession(userId, $"{userId}%3A%3A{accessToken}");
    }
}
