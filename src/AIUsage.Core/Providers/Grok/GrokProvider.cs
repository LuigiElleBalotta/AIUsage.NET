using AIUsage.Core.Models;
using AIUsage.Core.Pricing;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Grok;

public sealed class GrokProvider : IProviderRuntime
{
    public Provider Provider { get; } = new(
        "grok", "Grok", "grok",
        new List<ProviderLink> { new("Usage", "https://grok.com/?_s=usage") });

    public GrokAuthStore AuthStore { get; }
    public GrokUsageClient UsageClient { get; }
    public GrokLogUsageScanner LogUsageScanner { get; }
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Task<ModelPricing>> _pricing;

    public GrokProvider(
        GrokAuthStore? authStore = null,
        GrokUsageClient? usageClient = null,
        GrokLogUsageScanner? logUsageScanner = null,
        Func<DateTimeOffset>? now = null,
        Func<Task<ModelPricing>>? pricing = null)
    {
        AuthStore = authStore ?? new GrokAuthStore();
        UsageClient = usageClient ?? new GrokUsageClient();
        LogUsageScanner = logUsageScanner ?? new GrokLogUsageScanner();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pricing = pricing ?? (() => Task.FromResult(ModelPricingStore.Shared.Current()));
    }

    public List<WidgetDescriptor> WidgetDescriptors => new List<WidgetDescriptor>
    {
        WidgetDescriptorFactories.Percent("grok.weekly", Provider, "Weekly", metricLabel: "Weekly limit").ExportingLimit("weekly", unit: "percent"),
        WidgetDescriptorFactories.Badge("grok.payAsYouGo", Provider, "Extra Usage", metricLabel: "Pay as you go"),
        WidgetDescriptorFactories.UsageTrend(Provider).ExportingHistory(UsageHistoryScope.MachineLocal, true, "From your Grok logs (estimated)")
    }.Concat(WidgetDescriptorFactories.SpendTiles(Provider)).ToList();

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try { return AuthStore.LoadAuthCandidates().Count > 0; }
            catch { return false; }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await LoadAndProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ProviderSnapshot.Error(Provider, error, _now());
        }
    }

    private async Task<ProviderSnapshot> LoadAndProbeAsync(CancellationToken cancellationToken)
    {
        var candidates = await Task.Run(() => AuthStore.LoadAuthCandidates(), cancellationToken).ConfigureAwait(false);
        var sawExpiredCandidate = false;

        foreach (var state in candidates)
        {
            if (AuthStore.NeedsRefresh(state.Entry, state.Token))
            {
                var refreshed = await RefreshAccessTokenAsync(state).ConfigureAwait(false);
                if (refreshed is not null)
                {
                    return await ProbeAsync(state, refreshed, cancellationToken).ConfigureAwait(false);
                }
                if (AuthStore.IsExpired(state.Entry, state.Token))
                {
                    sawExpiredCandidate = true;
                    continue;
                }
            }
            return await ProbeAsync(state, state.Token, cancellationToken).ConfigureAwait(false);
        }

        throw new GrokAuthError(sawExpiredCandidate ? GrokAuthErrorKind.Expired : GrokAuthErrorKind.InvalidAuth);
    }

    private async Task<ProviderSnapshot> ProbeAsync(GrokAuthState state, string accessToken, CancellationToken cancellationToken)
    {
        var creditsResponse = await FetchCreditsConfigWithRetryAsync(accessToken, state).ConfigureAwait(false);
        var mapped = GrokUsageMapper.MapCreditsConfig(creditsResponse);

        var plan = await FetchPlanNameAsync(state.Token).ConfigureAwait(false);

        ProviderUsageHistory? usageHistory = null;
        var pricing = await _pricing().ConfigureAwait(false);
        var scan = await LogUsageScanner.ScanAsync(30, _now(), pricing, cancellationToken).ConfigureAwait(false);
        if (scan is not null)
        {
            usageHistory = new ProviderUsageHistory(scan.Series, scan.ModelUsage, scan.UnknownModelsByDay);
            SpendTileMapper.AppendTokenUsage(scan.Series, mapped.Lines, _now(), unknownModelsByDay: scan.UnknownModelsByDay, modelUsage: scan.ModelUsage, modelSourceNote: "From your Grok logs (estimated)");
            SpendTileMapper.AppendUsageTrend(scan.Series, mapped.Lines, _now(), "From your Grok logs (estimated)");
        }

        return ProviderSnapshot.Make(Provider, plan, mapped.Lines, _now(), usageHistory);
    }

    private async Task<HttpResponseResult> FetchCreditsConfigWithRetryAsync(string accessToken, GrokAuthState state)
    {
        return await ProviderAuthRetry.FetchAsync(
            accessToken,
            token => UsageClient.FetchCreditsConfigAsync(token),
            async () =>
            {
                var refreshed = await RefreshAccessTokenAsync(state).ConfigureAwait(false);
                if (refreshed is null) throw new GrokAuthError(GrokAuthErrorKind.Expired);
                return refreshed;
            },
            () => new GrokUsageError(GrokUsageErrorKind.ConnectionFailed),
            () => new GrokAuthError(GrokAuthErrorKind.Expired)
        ).ConfigureAwait(false);
    }

    private async Task<string?> RefreshAccessTokenAsync(GrokAuthState state)
    {
        var refreshToken = AuthStore.RefreshToken(state.Entry);
        if (refreshToken is null) return null;

        HttpResponseResult response;
        try
        {
            response = await UsageClient.RefreshTokenAsync(refreshToken, AuthStore.ClientId(state.EntryKey, state.Entry)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.AuthFor("grok"), $"token refresh request failed (transport): {ex.Message}");
            return null;
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            AppLog.Warn(LogTag.AuthFor("grok"), $"token refresh failed (HTTP {response.StatusCode})");
            return null;
        }
        var decoded = UsageClient.DecodeRefreshResponse(response);
        if (decoded is null || string.IsNullOrWhiteSpace(decoded.AccessToken))
        {
            AppLog.Warn(LogTag.AuthFor("grok"), "token refresh returned an undecodable or empty access token");
            return null;
        }

        var accessToken = decoded.AccessToken.Trim();
        state.Token = accessToken;
        state.Entry.Key = accessToken;
        if (!string.IsNullOrWhiteSpace(decoded.RefreshToken)) state.Entry.RefreshToken = decoded.RefreshToken!.Trim();
        if (!string.IsNullOrWhiteSpace(decoded.IdToken)) state.Entry.IdToken = decoded.IdToken!.Trim();

        var expiresAt = RefreshExpiryDate(decoded, accessToken);
        state.Entry.ExpiresAt = AIUsageISO8601.ToStringIso(expiresAt);
        try
        {
            AuthStore.Save(state);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogTag.AuthFor("grok"), $"failed to persist rotated credentials; using the refreshed token for this session only: {ex.Message}");
        }
        return accessToken;
    }

    private DateTimeOffset RefreshExpiryDate(GrokRefreshResponse response, string accessToken)
    {
        if (response.ExpiresIn is { } expiresIn && double.IsFinite(expiresIn) && expiresIn > 0)
        {
            return _now().AddSeconds(expiresIn);
        }
        if (AuthStore.TokenExpiresAt(accessToken) is { } tokenExpiry) return tokenExpiry;
        return _now().AddHours(1);
    }

    private async Task<string?> FetchPlanNameAsync(string accessToken)
    {
        try
        {
            return GrokUsageMapper.PlanName(await UsageClient.FetchSettingsAsync(accessToken).ConfigureAwait(false));
        }
        catch
        {
            return null;
        }
    }
}
