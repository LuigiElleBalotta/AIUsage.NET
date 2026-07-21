using AIUsage.Core.Models;

namespace AIUsage.Core.Providers.Devin;

public sealed class DevinProvider : IProviderRuntime
{
    public Provider Provider { get; } = new(
        "devin", "Devin", "devin",
        new List<ProviderLink> { new("Dashboard", "https://app.devin.ai/settings/plans") });

    public DevinAuthStore AuthStore { get; }
    public DevinUsageClient UsageClient { get; }
    private readonly Func<DateTimeOffset> _now;

    public DevinProvider(DevinAuthStore? authStore = null, DevinUsageClient? usageClient = null, Func<DateTimeOffset>? now = null)
    {
        AuthStore = authStore ?? new DevinAuthStore();
        UsageClient = usageClient ?? new DevinUsageClient();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public List<WidgetDescriptor> WidgetDescriptors => new()
    {
        WidgetDescriptorFactories.Percent("devin.daily", Provider, "Daily", metricLabel: "Daily quota").ExportingLimit("daily", unit: "percent"),
        WidgetDescriptorFactories.Percent("devin.weekly", Provider, "Weekly", metricLabel: "Weekly quota").ExportingLimit("weekly", unit: "percent"),
        WidgetDescriptorFactories.DollarBalance("devin.extra", Provider, "Extra Balance", "left", metricLabel: "Extra usage balance")
            .ExportingLimit("extraUsageBalance", LimitResourceDescriptor.ResourceKind.Balance, "usd", new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Dollars))
    };

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (AuthStore.LoadCredentialsFile() is not null) return true;
            return AuthStore.LoadAppAuth() is not null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private enum AttemptOutcome { Success, AuthFailure, Unavailable }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var sawApiKey = false;
        var sawAuthFailure = false;
        var credentials = await Task.Run(() => AuthStore.LoadCredentialsFile(), cancellationToken).ConfigureAwait(false);

        if (credentials is not null)
        {
            sawApiKey = true;
            var (outcome, mapped) = await AttemptAsync(credentials).ConfigureAwait(false);
            if (outcome == AttemptOutcome.Success) return Snapshot(mapped!);
            if (outcome == AttemptOutcome.AuthFailure) sawAuthFailure = true;
        }

        var appAuth = await Task.Run(() => AuthStore.LoadAppAuth(), cancellationToken).ConfigureAwait(false);
        if (appAuth is not null && (credentials is null || ShouldAttemptAppAuth(appAuth, credentials)))
        {
            sawApiKey = true;
            var (outcome, mapped) = await AttemptAsync(appAuth).ConfigureAwait(false);
            if (outcome == AttemptOutcome.Success) return Snapshot(mapped!);
            if (outcome == AttemptOutcome.AuthFailure) sawAuthFailure = true;
        }

        if (sawAuthFailure) return ProviderSnapshot.Error(Provider, new DevinAuthError(DevinAuthErrorKind.NotLoggedIn), _now());
        if (sawApiKey) return ProviderSnapshot.Error(Provider, new DevinUsageError(DevinUsageErrorKind.QuotaUnavailable), _now());
        return ProviderSnapshot.Error(Provider, new DevinAuthError(DevinAuthErrorKind.NotLoggedIn), _now());
    }

    private async Task<(AttemptOutcome, DevinMappedUsage?)> AttemptAsync(DevinAuth auth)
    {
        var apiServerUrl = AuthStore.EffectiveApiServerUrl(auth);
        try
        {
            var response = await UsageClient.FetchUserStatusAsync(auth, apiServerUrl).ConfigureAwait(false);
            if (response.StatusCode is 401 or 403) return (AttemptOutcome.AuthFailure, null);
            if (response.StatusCode is < 200 or >= 300) return (AttemptOutcome.Unavailable, null);
            return (AttemptOutcome.Success, DevinUsageMapper.MapUserStatusResponse(response));
        }
        catch
        {
            return (AttemptOutcome.Unavailable, null);
        }
    }

    private bool ShouldAttemptAppAuth(DevinAuth appAuth, DevinAuth? credentials)
    {
        if (credentials is null) return true;
        return appAuth.ApiKey != credentials.ApiKey || AuthStore.EffectiveApiServerUrl(appAuth) != AuthStore.EffectiveApiServerUrl(credentials);
    }

    private ProviderSnapshot Snapshot(DevinMappedUsage mapped) => ProviderSnapshot.Make(Provider, mapped.Plan, mapped.Lines, _now());
}
