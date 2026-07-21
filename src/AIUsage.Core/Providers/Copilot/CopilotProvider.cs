using AIUsage.Core.Models;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Copilot;

public sealed class CopilotProvider : IProviderRuntime
{
    public const string BillingOrgSettingsKey = "copilot.billingOrg";

    public Provider Provider { get; } = new(
        "copilot", "Copilot", "copilot",
        new List<ProviderLink>
        {
            new("Status", "https://www.githubstatus.com/"),
            new("Dashboard", "https://github.com/settings/billing")
        });

    public CopilotAuthStore AuthStore { get; }
    public CopilotUsageClient UsageClient { get; }
    public CopilotOrgBillingClient OrgBillingClient { get; }
    private readonly ISettingsStore _settings;
    private readonly Func<DateTimeOffset> _now;

    public CopilotProvider(
        CopilotAuthStore? authStore = null,
        CopilotUsageClient? usageClient = null,
        CopilotOrgBillingClient? orgBillingClient = null,
        ISettingsStore? settings = null,
        Func<DateTimeOffset>? now = null)
    {
        AuthStore = authStore ?? new CopilotAuthStore();
        UsageClient = usageClient ?? new CopilotUsageClient();
        OrgBillingClient = orgBillingClient ?? new CopilotOrgBillingClient();
        _settings = settings ?? FileSettingsStore.Shared;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public List<WidgetDescriptor> WidgetDescriptors => new()
    {
        WidgetDescriptorFactories.Percent("copilot.premium", Provider, "Credits").ExportingLimit("premiumCredits", unit: "percent"),
        WidgetDescriptorFactories.Values("copilot.extra", Provider, "Extra Usage", selection: new ValueSelection.OfKind(MetricKind.Count))
            .ExportingLimit("extraUsage", unit: "count", source: new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Count)),
        WidgetDescriptorFactories.Values("copilot.orgCredits", Provider, "Org Credits", selection: new ValueSelection.OfKind(MetricKind.Count))
            .ExportingLimit("orgCredits", unit: "credits", source: new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Count, "credits")),
        WidgetDescriptorFactories.Values("copilot.orgSpend", Provider, "Org Spend", selection: new ValueSelection.OfKind(MetricKind.Dollars), valueWord: "spent")
            .ExportingLimit("orgSpend", unit: "usd", source: new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Dollars)),
        WidgetDescriptorFactories.Percent("copilot.chat", Provider, "Chat").ExportingLimit("chat", unit: "percent"),
        WidgetDescriptorFactories.Percent("copilot.completions", Provider, "Completions").ExportingLimit("completions", unit: "percent")
    };

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => AuthStore.LoadToken() is not null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var token = await Task.Run(() => AuthStore.LoadToken(), cancellationToken).ConfigureAwait(false);
        if (token is null) return ProviderSnapshot.Error(Provider, new CopilotAuthError(CopilotAuthErrorKind.NotLoggedIn), _now());

        try
        {
            var response = await UsageClient.FetchUsageAsync(token.Value).ConfigureAwait(false);
            if (response.StatusCode is 401 or 403) return ProviderSnapshot.Error(Provider, new CopilotAuthError(CopilotAuthErrorKind.TokenInvalid), _now());
            if (response.StatusCode is < 200 or >= 300) return ProviderSnapshot.Error(Provider, new CopilotUsageError(CopilotUsageErrorKind.RequestFailed, response.StatusCode), _now());

            var mapped = CopilotUsageMapper.Map(response);
            var lines = mapped.Lines;
            if (mapped.IsOrgManagedSeat)
            {
                lines = await OrgBillingLinesAsync(token.Value).ConfigureAwait(false);
            }
            return ProviderSnapshot.Make(Provider, mapped.Plan, lines, _now());
        }
        catch (CopilotUsageError error)
        {
            return ProviderSnapshot.Error(Provider, error, _now());
        }
        catch
        {
            return ProviderSnapshot.Error(Provider, new CopilotUsageError(CopilotUsageErrorKind.ConnectionFailed), _now());
        }
    }

    private async Task<List<MetricLine>> OrgBillingLinesAsync(string token)
    {
        var cached = _settings.GetString(BillingOrgSettingsKey);
        if (cached is not null)
        {
            try
            {
                var lines = await UsageLinesForOrgAsync(cached, token).ConfigureAwait(false);
                if (lines is not null) return lines;
                _settings.Remove(BillingOrgSettingsKey);
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogTag.Plugin("copilot"), $"org billing lookup failed for the remembered org: {ex.Message}");
                return new List<MetricLine>();
            }
        }

        List<string> orgs;
        try
        {
            var response = await OrgBillingClient.FetchUserOrgsAsync(token).ConfigureAwait(false);
            if (response.StatusCode != 200)
            {
                AppLog.Info(LogTag.Plugin("copilot"), $"org list HTTP {response.StatusCode}; skipping org billing lookup");
                return new List<MetricLine>();
            }
            orgs = CopilotOrgBillingMapper.OrgLogins(response);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Plugin("copilot"), $"org list fetch failed: {ex.Message}");
            return new List<MetricLine>();
        }

        foreach (var org in orgs)
        {
            try
            {
                var lines = await UsageLinesForOrgAsync(org, token).ConfigureAwait(false);
                if (lines is not null)
                {
                    _settings.SetString(BillingOrgSettingsKey, org);
                    return lines;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogTag.Plugin("copilot"), $"org billing summary failed for one org; trying the next: {ex.Message}");
            }
        }
        return new List<MetricLine>();
    }

    private async Task<List<MetricLine>?> UsageLinesForOrgAsync(string org, string token)
    {
        var response = await OrgBillingClient.FetchUsageSummaryAsync(org, token).ConfigureAwait(false);
        if (response.StatusCode != 200)
        {
            AppLog.Debug(LogTag.Plugin("copilot"), $"org billing summary for one org: HTTP {response.StatusCode}");
            if (response.StatusCode == 429 || response.StatusCode >= 500)
            {
                throw new CopilotUsageError(CopilotUsageErrorKind.RequestFailed, response.StatusCode);
            }
            return null;
        }
        return CopilotOrgBillingMapper.UsageLines(response);
    }
}
