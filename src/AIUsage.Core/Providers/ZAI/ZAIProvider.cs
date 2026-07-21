using AIUsage.Core.Models;
using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.ZAI;

public sealed class ZAIProvider : IApiKeyManaging
{
    public Provider Provider { get; } = new(
        "zai", "Z.ai", "zai",
        new List<ProviderLink>
        {
            new("Dashboard", "https://z.ai/manage-apikey/coding-plan/personal/my-plan"),
            new("API Keys", "https://z.ai/manage-apikey/apikey-list")
        });

    public ZAIAuthStore AuthStore { get; }
    public ZAIUsageClient UsageClient { get; }
    private readonly Func<DateTimeOffset> _now;

    public ZAIProvider(ZAIAuthStore? authStore = null, ZAIUsageClient? usageClient = null, Func<DateTimeOffset>? now = null)
    {
        AuthStore = authStore ?? new ZAIAuthStore();
        UsageClient = usageClient ?? new ZAIUsageClient();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public List<WidgetDescriptor> WidgetDescriptors => new()
    {
        WidgetDescriptorFactories.Percent("zai.session", Provider, "Session", metricLabel: "Session").ExportingLimit("session", unit: "percent"),
        WidgetDescriptorFactories.Percent("zai.weekly", Provider, "Weekly", metricLabel: "Weekly").ExportingLimit("weekly", unit: "percent"),
        WidgetDescriptorFactories.BoundedCount("zai.webSearches", Provider, "Web Searches", 1000, "searches", metricLabel: "Web Searches", periodDurationMs: ZAIUsageMapper.MonthlyPeriodMs)
            .ExportingLimit("webSearches", unit: "searches")
    };

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => AuthStore.LoadAPIKey() is not null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var auth = await Task.Run(() => AuthStore.LoadAPIKey(), cancellationToken).ConfigureAwait(false);
        if (auth is null) return ProviderSnapshot.Error(Provider, new ZAIAuthError(ZAIAuthErrorKind.MissingKey), _now());

        var quota = await LoadAsync(() => UsageClient.FetchQuotaAsync(auth.ApiKey)).ConfigureAwait(false);
        var subscription = await LoadOptionalAsync(() => UsageClient.FetchSubscriptionAsync(auth.ApiKey)).ConfigureAwait(false);

        if (quota.IsAuthFailure) return ProviderSnapshot.Error(Provider, new ZAIAuthError(ZAIAuthErrorKind.InvalidKey), _now());
        if (quota.FailureError is { } err) return ProviderSnapshot.Error(Provider, err, _now());
        if (quota.Body is not { } body) return ProviderSnapshot.Error(Provider, new ZAIUsageError(ZAIUsageErrorKind.InvalidResponse), _now());

        if (ZAIUsageMapper.IsNoCodingPlan(body))
        {
            return ProviderSnapshot.Error(Provider, new ZAIUsageError(ZAIUsageErrorKind.NoCodingPlan), _now());
        }
        try
        {
            var (plan, lines) = ZAIUsageMapper.Map(body, subscription);
            return ProviderSnapshot.Make(Provider, plan, lines, _now());
        }
        catch (Exception error)
        {
            return ProviderSnapshot.Error(Provider, error, _now());
        }
    }

    private sealed record QuotaResult(byte[]? Body, bool IsAuthFailure, ZAIUsageError? FailureError);

    private async Task<QuotaResult> LoadAsync(Func<Task<HttpResponseResult>> call)
    {
        try
        {
            var response = await call().ConfigureAwait(false);
            if (response.StatusCode is 401 or 403) return new QuotaResult(null, true, null);
            if (response.StatusCode is < 200 or >= 300) return new QuotaResult(null, false, new ZAIUsageError(ZAIUsageErrorKind.RequestFailed, response.StatusCode));
            return new QuotaResult(response.Body, false, null);
        }
        catch
        {
            return new QuotaResult(null, false, new ZAIUsageError(ZAIUsageErrorKind.ConnectionFailed));
        }
    }

    private async Task<byte[]?> LoadOptionalAsync(Func<Task<HttpResponseResult>> call)
    {
        try
        {
            var response = await call().ConfigureAwait(false);
            return response.StatusCode is >= 200 and < 300 ? response.Body : null;
        }
        catch
        {
            return null;
        }
    }

    public APIKeyStatus ApiKeyStatus => AuthStore.KeyStatus();
    public string? CurrentApiKey() => AuthStore.CurrentAPIKey();
    public void SaveApiKey(string key) => AuthStore.SaveAPIKey(key);
    public void DeleteApiKey() => AuthStore.DeleteAPIKey();
}
