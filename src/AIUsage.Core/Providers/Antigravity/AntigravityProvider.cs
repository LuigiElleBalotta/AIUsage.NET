using AIUsage.Core.Models;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Antigravity;

public sealed class AntigravityProvider : IProviderRuntime
{
    public Provider Provider { get; } = new("antigravity", "Antigravity", "antigravity");

    public AntigravityAuthStore AuthStore { get; }
    public AntigravityUsageClient UsageClient { get; }
    public LanguageServerDiscovery Discovery { get; }
    private readonly Func<DateTimeOffset> _now;

    public AntigravityProvider(
        AntigravityAuthStore? authStore = null,
        AntigravityUsageClient? usageClient = null,
        LanguageServerDiscovery? discovery = null,
        Func<DateTimeOffset>? now = null)
    {
        AuthStore = authStore ?? new AntigravityAuthStore();
        UsageClient = usageClient ?? new AntigravityUsageClient();
        Discovery = discovery ?? new LanguageServerDiscovery();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public List<WidgetDescriptor> WidgetDescriptors => new()
    {
        WidgetDescriptorFactories.Percent(AntigravityMetric.GeminiId, Provider, AntigravityMetric.SessionLabel, isSessionWindow: true).ExportingLimit("geminiSession", unit: "percent"),
        WidgetDescriptorFactories.Percent(AntigravityMetric.GeminiWeeklyId, Provider, AntigravityMetric.WeeklyLabel).ExportingLimit("geminiWeekly", unit: "percent"),
        WidgetDescriptorFactories.Percent(AntigravityMetric.ClaudeId, Provider, AntigravityMetric.ClaudeLabel, isSessionWindow: true).ExportingLimit("nonGeminiSession", unit: "percent"),
        WidgetDescriptorFactories.Percent(AntigravityMetric.ClaudeWeeklyId, Provider, AntigravityMetric.ClaudeWeeklyLabel).ExportingLimit("nonGeminiWeekly", unit: "percent")
    };

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var keychainToken = AuthStore.LoadKeychainToken();
                if (keychainToken is null)
                {
                    AuthStore.DiscardCachedToken();
                    return false;
                }
                return true;
            }
            catch
            {
                return true;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private sealed record StrategyResult(string? Plan, List<MetricLine> Lines);

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProbeAsync().ConfigureAwait(false);
            return ProviderSnapshot.Make(Provider, result.Plan, result.Lines, _now());
        }
        catch (Exception error)
        {
            return ProviderSnapshot.Error(Provider, error, _now());
        }
    }

    private async Task<StrategyResult> ProbeAsync()
    {
        if (await ProbeLsAsync("language_server", new[] { "antigravity", "antigravity-ide" }, "--csrf_token", "--extension_server_port").ConfigureAwait(false) is { } r1) return r1;
        if (await ProbeLsAsync("agy", Array.Empty<string>(), "", null).ConfigureAwait(false) is { } r2) return r2;
        return await ProbeCloudCodeAsync().ConfigureAwait(false);
    }

    // MARK: - Language server

    private async Task<StrategyResult?> ProbeLsAsync(string processName, string[] markers, string csrfFlag, string? portFlag)
    {
        var options = new LanguageServerDiscovery.Options(processName, markers, csrfFlag, portFlag);
        var discovered = await Task.Run(() => Discovery.Discover(options)).ConfigureAwait(false);
        if (discovered is null) return null;

        var endpoints = new List<(string Scheme, int Port)>();
        foreach (var port in discovered.Ports)
        {
            endpoints.Add(("https", port));
            endpoints.Add(("http", port));
        }
        if (discovered.ExtensionPort is { } ext) endpoints.Add(("http", ext));

        foreach (var endpoint in endpoints)
        {
            var summary = await UsageClient.CallLsAsync(endpoint.Scheme, endpoint.Port, discovered.Csrf, "RetrieveUserQuotaSummary").ConfigureAwait(false);
            if (summary is not null)
            {
                if (summary.StatusCode is >= 200 and < 300)
                {
                    var lines = AntigravityUsageMapper.ParseQuotaSummary(summary.Body);
                    if (lines is not null)
                    {
                        string? plan = null;
                        var status = await UsageClient.CallLsAsync(endpoint.Scheme, endpoint.Port, discovered.Csrf, "GetUserStatus").ConfigureAwait(false);
                        if (status is { StatusCode: >= 200 and < 300 })
                        {
                            plan = AntigravityUsageMapper.ParseUserStatus(status.Body)?.Plan;
                        }
                        return new StrategyResult(plan, lines);
                    }
                }
                else if (summary.StatusCode != 404)
                {
                    AppLog.Warn(LogTag.Plugin("antigravity"), $"RetrieveUserQuotaSummary HTTP {summary.StatusCode}; falling back to legacy quota endpoints");
                }
            }

            var response = await UsageClient.CallLsAsync(endpoint.Scheme, endpoint.Port, discovered.Csrf, "GetUserStatus").ConfigureAwait(false);
            if (response is not { StatusCode: >= 200 and < 300 }) continue;

            var parsed = AntigravityUsageMapper.ParseUserStatus(response.Body);
            if (parsed is not null)
            {
                var lines = AntigravityUsageMapper.BuildLines(parsed.Value.Configs);
                if (lines.Count > 0) return new StrategyResult(parsed.Value.Plan, lines);
            }

            var fallback = await UsageClient.CallLsAsync(endpoint.Scheme, endpoint.Port, discovered.Csrf, "GetCommandModelConfigs").ConfigureAwait(false);
            if (fallback is { StatusCode: >= 200 and < 300 } && AntigravityUsageMapper.ParseCommandModelConfigs(fallback.Body) is { } configs)
            {
                var lines = AntigravityUsageMapper.BuildLines(configs);
                if (lines.Count > 0) return new StrategyResult(null, lines);
            }
        }
        return null;
    }

    // MARK: - Cloud Code

    private async Task<StrategyResult> ProbeCloudCodeAsync()
    {
        var keychainToken = await Task.Run(() => AuthStore.LoadKeychainToken()).ConfigureAwait(false);
        if (keychainToken is null)
        {
            AuthStore.DiscardCachedToken();
            throw new AntigravityError(AntigravityErrorKind.NotSignedIn);
        }

        var tokens = new List<string>();
        if (keychainToken.AccessToken is { } access && AuthStore.IsUsable(keychainToken.Expiry)) tokens.Add(access);
        var cached = await Task.Run(() => AuthStore.LoadCachedToken(keychainToken)).ConfigureAwait(false);
        if (cached is not null && !tokens.Contains(cached)) tokens.Add(cached);

        var hasCredentials = tokens.Count > 0 || !string.IsNullOrEmpty(keychainToken.RefreshToken);

        var sawAuthFailure = false;
        foreach (var token in tokens)
        {
            var outcome = await FetchCloudCodeAsync(token).ConfigureAwait(false);
            if (outcome.Result is not null) return outcome.Result;
            if (outcome.AuthFailed) sawAuthFailure = true;
        }

        if ((sawAuthFailure || tokens.Count == 0) && keychainToken.RefreshToken is { } refreshToken)
        {
            var refreshOutcome = await UsageClient.RefreshGoogleTokenAsync(refreshToken).ConfigureAwait(false);
            switch (refreshOutcome)
            {
                case TokenRefreshOutcome.Refreshed refreshed:
                    await Task.Run(() => AuthStore.CacheToken(refreshed.AccessToken, refreshed.ExpiresIn, refreshToken)).ConfigureAwait(false);
                    var retryOutcome = await FetchCloudCodeAsync(refreshed.AccessToken).ConfigureAwait(false);
                    if (retryOutcome.Result is not null) return retryOutcome.Result;
                    if (retryOutcome.AuthFailed) throw new AntigravityError(AntigravityErrorKind.AuthExpired);
                    throw new AntigravityError(AntigravityErrorKind.Unavailable);
                case TokenRefreshOutcome.AuthFailed:
                    throw new AntigravityError(AntigravityErrorKind.AuthExpired);
                default:
                    throw new AntigravityError(AntigravityErrorKind.Unavailable);
            }
        }

        if (sawAuthFailure) throw new AntigravityError(AntigravityErrorKind.AuthExpired);
        if (hasCredentials) throw new AntigravityError(AntigravityErrorKind.Unavailable);
        throw new AntigravityError(AntigravityErrorKind.NotSignedIn);
    }

    private sealed record CloudCodeProbeOutcome(StrategyResult? Result, bool AuthFailed);

    private async Task<CloudCodeProbeOutcome> FetchCloudCodeAsync(string token)
    {
        var summaryOutcome = await UsageClient.CloudCodeAsync(AntigravityUsageClient.QuotaSummaryPath, token, "antigravity", new()).ConfigureAwait(false);
        switch (summaryOutcome)
        {
            case CloudCodeOutcome.AuthFailed:
                return new CloudCodeProbeOutcome(null, true);
            case CloudCodeOutcome.Ok ok:
                var lines = AntigravityUsageMapper.ParseQuotaSummary(ok.Data);
                if (lines is not null)
                {
                    return new CloudCodeProbeOutcome(new StrategyResult(await LoadPlanAsync(token).ConfigureAwait(false), lines), false);
                }
                break;
        }

        var modelsOutcome = await UsageClient.CloudCodeAsync(AntigravityUsageClient.FetchModelsPath, token, "antigravity", new()).ConfigureAwait(false);
        switch (modelsOutcome)
        {
            case CloudCodeOutcome.AuthFailed:
                return new CloudCodeProbeOutcome(null, true);
            case CloudCodeOutcome.Ok ok:
                var modelLines = AntigravityUsageMapper.BuildLines(AntigravityUsageMapper.ParseCloudCodeModels(ok.Data));
                if (modelLines.Count > 0)
                {
                    return new CloudCodeProbeOutcome(new StrategyResult(await LoadPlanAsync(token).ConfigureAwait(false), modelLines), false);
                }
                break;
        }

        string? plan = null;
        string? project = null;
        var loadOutcome = await UsageClient.CloudCodeAsync(AntigravityUsageClient.LoadCodeAssistPath, token, "agy", new()).ConfigureAwait(false);
        switch (loadOutcome)
        {
            case CloudCodeOutcome.AuthFailed:
                return new CloudCodeProbeOutcome(null, true);
            case CloudCodeOutcome.Ok ok:
                plan = AntigravityUsageMapper.ParseLoadCodeAssistPlan(ok.Data);
                project = AntigravityUsageMapper.ParseProject(ok.Data);
                break;
        }

        var quotaBody = project is not null ? new Dictionary<string, string> { ["project"] = project } : new();
        var quotaOutcome = await UsageClient.CloudCodeAsync(AntigravityUsageClient.RetrieveQuotaPath, token, "agy", quotaBody).ConfigureAwait(false);
        if (quotaOutcome is CloudCodeOutcome.Unavailable && project is not null)
        {
            quotaOutcome = await UsageClient.CloudCodeAsync(AntigravityUsageClient.RetrieveQuotaPath, token, "agy", new()).ConfigureAwait(false);
        }
        switch (quotaOutcome)
        {
            case CloudCodeOutcome.AuthFailed:
                return new CloudCodeProbeOutcome(null, true);
            case CloudCodeOutcome.Ok ok:
                var quotaLines = AntigravityUsageMapper.BuildLines(AntigravityUsageMapper.ParseQuotaBuckets(ok.Data));
                if (quotaLines.Count > 0) return new CloudCodeProbeOutcome(new StrategyResult(plan, quotaLines), false);
                break;
        }
        return new CloudCodeProbeOutcome(null, false);
    }

    private async Task<string?> LoadPlanAsync(string token)
    {
        var outcome = await UsageClient.CloudCodeAsync(AntigravityUsageClient.LoadCodeAssistPath, token, "agy", new()).ConfigureAwait(false);
        return outcome is CloudCodeOutcome.Ok ok ? AntigravityUsageMapper.ParseLoadCodeAssistPlan(ok.Data) : null;
    }
}
