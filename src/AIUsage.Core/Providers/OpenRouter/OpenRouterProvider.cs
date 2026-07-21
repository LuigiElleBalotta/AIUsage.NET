using AIUsage.Core.Models;
using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.OpenRouter;

public sealed class OpenRouterProvider : IApiKeyManaging
{
    public Provider Provider { get; } = new(
        "openrouter", "OpenRouter", "openrouter",
        new List<ProviderLink>
        {
            new("Activity", "https://openrouter.ai/activity"),
            new("Credits", "https://openrouter.ai/settings/credits")
        });

    public OpenRouterAuthStore AuthStore { get; }
    public OpenRouterUsageClient UsageClient { get; }
    private readonly Func<DateTimeOffset> _now;

    public OpenRouterProvider(OpenRouterAuthStore? authStore = null, OpenRouterUsageClient? usageClient = null, Func<DateTimeOffset>? now = null)
    {
        AuthStore = authStore ?? new OpenRouterAuthStore();
        UsageClient = usageClient ?? new OpenRouterUsageClient();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public List<WidgetDescriptor> WidgetDescriptors => new()
    {
        WidgetDescriptorFactories.BoundedDollars("openrouter.credits", Provider, "Credits", 100, metricLabel: "Credits", limitNoun: "purchased")
            .ExportingLimit("credits", unit: "usd"),
        WidgetDescriptorFactories.DollarBalance("openrouter.balance", Provider, "Balance", "left", metricLabel: "Balance")
            .ExportingLimit("balance", LimitResourceDescriptor.ResourceKind.Balance, "usd", new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Dollars)),
        WidgetDescriptorFactories.Values("openrouter.today", Provider, "Today", metricLabel: "Today", selection: new ValueSelection.OfKind(MetricKind.Dollars), isUsagePeriod: true),
        WidgetDescriptorFactories.Values("openrouter.week", Provider, "This Week", metricLabel: "This Week", selection: new ValueSelection.OfKind(MetricKind.Dollars), isUsagePeriod: true),
        WidgetDescriptorFactories.Values("openrouter.month", Provider, "This Month", metricLabel: "This Month", selection: new ValueSelection.OfKind(MetricKind.Dollars), isUsagePeriod: true),
        WidgetDescriptorFactories.BoundedDollars("openrouter.keyLimit", Provider, "Key Limit", 100, metricLabel: "Key Limit", valueWord: "spent")
            .ExportingLimit("keyLimit", unit: "usd")
    };

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => AuthStore.LoadAPIKey() is not null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var auth = await Task.Run(() => AuthStore.LoadAPIKey(), cancellationToken).ConfigureAwait(false);
        if (auth is null) return ProviderSnapshot.Error(Provider, new OpenRouterAuthError(OpenRouterAuthErrorKind.MissingKey), _now());

        var credits = await LoadAsync(() => UsageClient.FetchCreditsAsync(auth.ApiKey)).ConfigureAwait(false);
        var key = await LoadAsync(() => UsageClient.FetchKeyAsync(auth.ApiKey)).ConfigureAwait(false);

        var lines = new List<MetricLine>();
        string? plan = null;
        if (credits.Data is { } creditsData) lines.AddRange(OpenRouterUsageMapper.CreditsLines(creditsData));
        if (key.Data is { } keyData)
        {
            var (mappedPlan, mappedLines) = OpenRouterUsageMapper.KeyMetrics(keyData);
            plan = mappedPlan;
            lines.AddRange(mappedLines);
        }

        if (lines.Count > 0) return ProviderSnapshot.Make(Provider, plan, lines, _now());

        if (credits.IsAuthFailure && key.IsAuthFailure)
        {
            return ProviderSnapshot.Error(Provider, new OpenRouterAuthError(OpenRouterAuthErrorKind.InvalidKey), _now());
        }
        var error = credits.FailureError ?? key.FailureError ?? new OpenRouterUsageError(OpenRouterUsageErrorKind.InvalidResponse);
        return ProviderSnapshot.Error(Provider, error, _now());
    }

    private sealed record EndpointResult(System.Text.Json.JsonElement? Data, bool IsAuthFailure, OpenRouterUsageError? FailureError);

    private async Task<EndpointResult> LoadAsync(Func<Task<HttpResponseResult>> call)
    {
        try
        {
            var response = await call().ConfigureAwait(false);
            if (response.StatusCode is 401 or 403) return new EndpointResult(null, true, null);
            if (response.StatusCode is < 200 or >= 300) return new EndpointResult(null, false, new OpenRouterUsageError(OpenRouterUsageErrorKind.RequestFailed, response.StatusCode));
            var data = OpenRouterUsageMapper.DataObject(response.Body);
            if (data is null) return new EndpointResult(null, false, new OpenRouterUsageError(OpenRouterUsageErrorKind.InvalidResponse));
            return new EndpointResult(data, false, null);
        }
        catch
        {
            return new EndpointResult(null, false, new OpenRouterUsageError(OpenRouterUsageErrorKind.ConnectionFailed));
        }
    }

    public APIKeyStatus ApiKeyStatus => AuthStore.KeyStatus();
    public string? CurrentApiKey() => AuthStore.CurrentAPIKey();
    public void SaveApiKey(string key) => AuthStore.SaveAPIKey(key);
    public void DeleteApiKey() => AuthStore.DeleteAPIKey();
}
