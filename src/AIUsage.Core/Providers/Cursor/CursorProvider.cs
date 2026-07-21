using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Pricing;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Cursor;

public sealed class CursorProvider : IProviderRuntime
{
    public Provider Provider { get; } = new(
        "cursor", "Cursor", "cursor",
        new List<ProviderLink>
        {
            new("Status", "https://status.cursor.com/"),
            new("Dashboard", "https://www.cursor.com/dashboard")
        });

    public CursorAuthStore AuthStore { get; }
    public CursorUsageClient UsageClient { get; }
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Task<ModelPricing>> _pricing;

    public CursorProvider(CursorAuthStore? authStore = null, CursorUsageClient? usageClient = null, Func<DateTimeOffset>? now = null, Func<Task<ModelPricing>>? pricing = null)
    {
        AuthStore = authStore ?? new CursorAuthStore();
        UsageClient = usageClient ?? new CursorUsageClient();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pricing = pricing ?? (() => Task.FromResult(ModelPricingStore.Shared.Current()));
    }

    public List<WidgetDescriptor> WidgetDescriptors => new List<WidgetDescriptor>
    {
        WidgetDescriptorFactories.Percent("cursor.usage", Provider, "Total Usage", metricLabel: "Total usage").ExportingLimit("totalUsage", unit: "percent"),
        WidgetDescriptorFactories.Percent("cursor.auto", Provider, "Auto Usage", metricLabel: "Auto usage").ExportingLimit("autoUsage", unit: "percent"),
        WidgetDescriptorFactories.Percent("cursor.api", Provider, "API Usage", metricLabel: "API usage").ExportingLimit("apiUsage", unit: "percent"),
        WidgetDescriptorFactories.BoundedDollars("cursor.onDemand", Provider, "Extra Usage", 100, metricLabel: "On-demand", valueWord: "spent")
            .ExportingLimit("onDemand", unit: "usd", source: new LimitResourceDescriptor.ResourceSource.ProgressOrValue(MetricKind.Dollars)),
        WidgetDescriptorFactories.BoundedCount("cursor.requests", Provider, "Requests", 500, "requests", periodDurationMs: CursorUsageMapper.BillingPeriodMs)
            .ExportingLimit("requests", unit: "requests"),
        WidgetDescriptorFactories.DollarBalance("cursor.credits", Provider, "Credits", "left").ExportingLimit("credits", LimitResourceDescriptor.ResourceKind.Balance, "usd", new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Dollars)),
        WidgetDescriptorFactories.UsageTrend(Provider).ExportingHistory(UsageHistoryScope.AccountWide, true, "From your Cursor usage export")
    }.Concat(WidgetDescriptorFactories.SpendTiles(Provider, WidgetData.CursorUsageHistoryNote)).ToList();

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => AuthStore.LoadAuthState() is not null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var state = await Task.Run(() => AuthStore.LoadAuthState(), cancellationToken).ConfigureAwait(false);
        if (state is null) return ProviderSnapshot.Error(Provider, new CursorAuthError(CursorAuthErrorKind.NotLoggedIn), _now());

        try
        {
            return await ProbeAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ProviderSnapshot.Error(Provider, error, _now());
        }
    }

    private async Task<ProviderSnapshot> ProbeAsync(CursorAuthState authState, CancellationToken cancellationToken)
    {
        var accessToken = authState.AccessToken?.Trim().NilIfEmpty();

        if (AuthStore.NeedsRefresh(accessToken))
        {
            try
            {
                var refreshed = await RefreshAccessTokenAsync(authState).ConfigureAwait(false);
                if (refreshed is not null)
                {
                    authState.AccessToken = refreshed;
                    accessToken = refreshed;
                }
                else if (accessToken is null)
                {
                    throw new CursorAuthError(CursorAuthErrorKind.NotLoggedIn);
                }
            }
            catch when (accessToken is not null)
            {
                // keep the still-valid access token; refresh failure is non-fatal here
            }
        }

        if (accessToken is null) throw new CursorAuthError(CursorAuthErrorKind.NotLoggedIn);

        var usageResponse = await FetchUsageWithRetryAsync(accessToken, authState).ConfigureAwait(false);
        ProviderAuthRetry.RequireSuccess(usageResponse, () => new CursorAuthError(CursorAuthErrorKind.TokenExpired), status => new CursorUsageError(CursorUsageErrorKind.RequestFailed, status));
        var usage = ProviderParse.JsonObject(usageResponse.Body) ?? throw new CursorUsageError(CursorUsageErrorKind.InvalidResponse);
        var currentToken = authState.AccessToken ?? accessToken;

        var (planName, planInfoUnavailable) = await FetchPlanNameAsync(currentToken).ConfigureAwait(false);
        var fallback = CursorUsageMapper.ShouldUseRequestBasedFallback(usage, planName, planInfoUnavailable);
        if (fallback.ShouldFallback)
        {
            var mapped = await UsageSummaryAndRequestResultAsync(currentToken, planName, fallback.Message).ConfigureAwait(false);
            var history = await AppendSpendLinesAsync(mapped.Lines, currentToken).ConfigureAwait(false);
            return Snapshot(mapped, history);
        }

        if (ShouldTryGenericRequestFallback(usage))
        {
            try
            {
                var mapped = await RequestBasedResultAsync(currentToken, planName, "Cursor request-based usage data unavailable. Try again later.").ConfigureAwait(false);
                return Snapshot(mapped);
            }
            catch
            {
                AppLog.Warn(LogTag.Plugin("cursor"), "optional request-based usage fallback failed");
            }
        }

        var creditGrants = await FetchCreditGrantsAsync(currentToken).ConfigureAwait(false);
        var stripeBalanceCents = await FetchStripeBalanceCentsAsync(currentToken).ConfigureAwait(false);
        var mapped2 = CursorUsageMapper.MapUsage(usage, planName, creditGrants, stripeBalanceCents);
        var history2 = await AppendSpendLinesAsync(mapped2.Lines, currentToken).ConfigureAwait(false);
        return Snapshot(mapped2, history2);
    }

    private async Task<ProviderUsageHistory?> AppendSpendLinesAsync(List<MetricLine> lines, string accessToken)
    {
        var end = _now();
        var startOfToday = end.Date;
        var start = startOfToday.AddDays(-29);

        HttpResponseResult? response;
        try
        {
            response = await UsageClient.FetchUsageCsvAsync(accessToken, start, end).ConfigureAwait(false);
        }
        catch
        {
            AppLog.Warn(LogTag.Plugin("cursor"), "usage CSV request failed");
            return null;
        }
        if (response is null)
        {
            AppLog.Warn(LogTag.Plugin("cursor"), "usage CSV request could not be prepared from the current session");
            return null;
        }
        if (response.StatusCode is < 200 or >= 300)
        {
            AppLog.Warn(LogTag.Plugin("cursor"), $"usage CSV request returned HTTP {response.StatusCode}");
            return null;
        }
        string csv;
        try { csv = System.Text.Encoding.UTF8.GetString(response.Body); } catch { return null; }

        var pricing = await _pricing().ConfigureAwait(false);
        try
        {
            var parsed = CursorUsageCsv.Parse(csv, pricing);
            if (parsed.RejectedRowCount > 0)
            {
                AppLog.Warn(LogTag.Plugin("cursor"), $"usage CSV ignored {parsed.RejectedRowCount} malformed row(s)");
            }
            return CursorUsageMapper.AppendSpendLines(parsed.Rows, end, pricing, lines);
        }
        catch (CursorUsageCsvError error)
        {
            if (error.Kind == CursorUsageCsvErrorKind.MissingColumns)
            {
                AppLog.Warn(LogTag.Plugin("cursor"), $"usage CSV missing required columns: {string.Join(", ", error.MissingColumns)}");
            }
            else
            {
                AppLog.Warn(LogTag.Plugin("cursor"), "usage CSV is structurally malformed");
            }
        }
        catch
        {
            AppLog.Warn(LogTag.Plugin("cursor"), "usage CSV could not be parsed");
        }
        return null;
    }

    private async Task<HttpResponseResult> FetchUsageWithRetryAsync(string accessToken, CursorAuthState authState)
    {
        return await ProviderAuthRetry.FetchAsync(
            accessToken,
            token => UsageClient.FetchUsageAsync(token),
            async () =>
            {
                var refreshed = await RefreshAccessTokenAsync(authState).ConfigureAwait(false);
                if (refreshed is null) throw new CursorAuthError(CursorAuthErrorKind.TokenExpired);
                authState.AccessToken = refreshed;
                return refreshed;
            },
            () => new CursorUsageError(CursorUsageErrorKind.ConnectionFailed),
            () => new CursorAuthError(CursorAuthErrorKind.TokenExpired),
            () => new CursorUsageError(CursorUsageErrorKind.UsageAfterRefreshFailed)
        ).ConfigureAwait(false);
    }

    private async Task<string?> RefreshAccessTokenAsync(CursorAuthState authState)
    {
        var refreshToken = authState.RefreshToken?.Trim().NilIfEmpty();
        if (refreshToken is null) return null;

        var response = await UsageClient.RefreshTokenAsync(refreshToken).ConfigureAwait(false);
        if (response.StatusCode is 400 or 401)
        {
            var body = ProviderParse.JsonObject(response.Body);
            if (body is { } b && b.TryGetProperty("shouldLogout", out var sl) && sl.ValueKind == JsonValueKind.True)
            {
                throw new CursorAuthError(CursorAuthErrorKind.SessionExpired);
            }
            throw new CursorAuthError(CursorAuthErrorKind.TokenExpired);
        }
        if (response.StatusCode is < 200 or >= 300) return null;
        var body2 = ProviderParse.JsonObject(response.Body);
        if (body2 is not { } b2) return null;
        if (b2.TryGetProperty("shouldLogout", out var sl2) && sl2.ValueKind == JsonValueKind.True)
        {
            throw new CursorAuthError(CursorAuthErrorKind.SessionExpired);
        }
        if (!b2.TryGetProperty("access_token", out var atEl) || atEl.ValueKind != JsonValueKind.String) return null;
        var accessToken = atEl.GetString()?.Trim().NilIfEmpty();
        if (accessToken is null) return null;

        try
        {
            AuthStore.SaveAccessToken(accessToken, authState.Source);
        }
        catch
        {
            AppLog.Error(LogTag.AuthFor("cursor"), "failed to persist rotated access token to the Cursor state DB; using it for this session only");
        }
        return accessToken;
    }

    private async Task<(string? PlanName, bool Unavailable)> FetchPlanNameAsync(string accessToken)
    {
        var body = await FetchOptionalJsonObjectAsync("plan", () => UsageClient.FetchPlanAsync(accessToken)).ConfigureAwait(false);
        if (body is not { } b) return (null, true);
        if (!b.TryGetProperty("planInfo", out var planInfo) || planInfo.ValueKind != JsonValueKind.Object ||
            !planInfo.TryGetProperty("planName", out var pn) || pn.ValueKind != JsonValueKind.String)
        {
            AppLog.Warn(LogTag.Plugin("cursor"), "optional plan response contained invalid plan metadata");
            return (null, true);
        }
        var planName = pn.GetString()?.Trim().NilIfEmpty();
        return (planName, false);
    }

    private async Task<JsonElement?> FetchCreditGrantsAsync(string accessToken)
    {
        var body = await FetchOptionalJsonObjectAsync("credit-grants", () => UsageClient.FetchCreditsAsync(accessToken)).ConfigureAwait(false);
        if (body is not { } b) return null;
        if (!b.TryGetProperty("hasCreditGrants", out var hcg) || hcg.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AppLog.Warn(LogTag.Plugin("cursor"), "optional credit-grants response contained invalid grant metadata");
            return null;
        }
        if (hcg.ValueKind == JsonValueKind.True)
        {
            if (ProviderParse.Number(GetOrNull(b, "totalCents")) is not { } tc || tc <= 0 ||
                ProviderParse.Number(GetOrNull(b, "usedCents")) is not { } uc || uc < 0)
            {
                AppLog.Warn(LogTag.Plugin("cursor"), "optional credit-grants response contained invalid grant metadata");
                return null;
            }
        }
        return b;
    }

    private async Task<double> FetchStripeBalanceCentsAsync(string accessToken)
    {
        var body = await FetchOptionalJsonObjectAsync("prepaid-balance", () => UsageClient.FetchStripeBalanceAsync(accessToken)).ConfigureAwait(false);
        if (body is not { } b) return 0;
        if (ProviderParse.Number(GetOrNull(b, "customerBalance")) is null)
        {
            AppLog.Warn(LogTag.Plugin("cursor"), "optional prepaid-balance response contained invalid balance metadata");
            return 0;
        }
        return CursorUsageMapper.StripeBalanceCents(b);
    }

    private async Task<JsonElement?> FetchOptionalJsonObjectAsync(string label, Func<Task<HttpResponseResult?>> request)
    {
        HttpResponseResult? response;
        try
        {
            response = await request().ConfigureAwait(false);
        }
        catch
        {
            AppLog.Warn(LogTag.Plugin("cursor"), $"optional {label} request failed");
            return null;
        }
        if (response is null)
        {
            AppLog.Warn(LogTag.Plugin("cursor"), $"optional {label} request could not be prepared from the current session");
            return null;
        }
        if (response.StatusCode is < 200 or >= 300)
        {
            AppLog.Warn(LogTag.Plugin("cursor"), $"optional {label} request returned HTTP {response.StatusCode}");
            return null;
        }
        var body = ProviderParse.JsonObject(response.Body);
        if (body is null) AppLog.Warn(LogTag.Plugin("cursor"), $"optional {label} response was invalid");
        return body;
    }

    private async Task<CursorMappedUsage> RequestBasedResultAsync(string accessToken, string? planName, string unavailableMessage)
    {
        try
        {
            var response = await UsageClient.FetchRequestBasedUsageAsync(accessToken).ConfigureAwait(false);
            if (response is not { } r || r.StatusCode is < 200 or >= 300 || ProviderParse.JsonObject(r.Body) is not { } body)
            {
                throw new CursorUsageError(CursorUsageErrorKind.RequestBasedUnavailable, message: unavailableMessage);
            }
            return CursorUsageMapper.MapRequestBasedUsage(body, planName, unavailableMessage);
        }
        catch (CursorUsageError)
        {
            throw;
        }
        catch
        {
            throw new CursorUsageError(CursorUsageErrorKind.RequestBasedUnavailable, message: unavailableMessage);
        }
    }

    private async Task<CursorMappedUsage> UsageSummaryAndRequestResultAsync(string accessToken, string? planName, string unavailableMessage)
    {
        var summary = await FetchOptionalJsonObjectAsync("usage-summary", () => UsageClient.FetchUsageSummaryAsync(accessToken)).ConfigureAwait(false);
        if (summary is { } s && !CursorUsageSummaryMapper.HasUsableSummaryPayload(s))
        {
            AppLog.Warn(LogTag.Plugin("cursor"), "optional usage-summary response contained no usable usage fields");
        }
        var requestUsage = await FetchOptionalJsonObjectAsync("request-based usage", () => UsageClient.FetchRequestBasedUsageAsync(accessToken)).ConfigureAwait(false);
        if (requestUsage is { } ru && !CursorUsageSummaryMapper.HasUsableRequestPayload(ru))
        {
            AppLog.Warn(LogTag.Plugin("cursor"), "optional request-based usage response contained no usable usage fields");
        }
        return CursorUsageSummaryMapper.Map(summary, requestUsage, planName, unavailableMessage);
    }

    private static bool ShouldTryGenericRequestFallback(JsonElement usage) => new CursorPlanUsageFacts(usage).ShouldTryGenericRequestFallback;

    private ProviderSnapshot Snapshot(CursorMappedUsage mapped, ProviderUsageHistory? usageHistory = null)
    {
        return ProviderSnapshot.Make(Provider, mapped.Plan, mapped.Lines, _now(), usageHistory);
    }

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
