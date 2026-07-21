using AIUsage.Core.Models;
using AIUsage.Core.Pricing;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Codex;

public sealed class CodexProvider : IProviderRuntime
{
    public Provider Provider { get; } = new(
        "codex", "Codex", "codex",
        new List<ProviderLink>
        {
            new("Status", "https://status.openai.com/"),
            new("Dashboard", "https://chatgpt.com/codex/settings/usage")
        });

    public CodexAuthStore AuthStore { get; }
    public CodexUsageClient UsageClient { get; }
    public CodexLogUsageScanner LogUsageScanner { get; }
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Task<ModelPricing>> _pricing;

    public CodexProvider(
        CodexAuthStore? authStore = null,
        CodexUsageClient? usageClient = null,
        CodexLogUsageScanner? logUsageScanner = null,
        Func<DateTimeOffset>? now = null,
        Func<Task<ModelPricing>>? pricing = null)
    {
        AuthStore = authStore ?? new CodexAuthStore();
        UsageClient = usageClient ?? new CodexUsageClient();
        LogUsageScanner = logUsageScanner ?? new CodexLogUsageScanner();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pricing = pricing ?? (() => Task.FromResult(ModelPricingStore.Shared.Current()));
    }

    public List<WidgetDescriptor> WidgetDescriptors => new List<WidgetDescriptor>
    {
        WidgetDescriptorFactories.Percent("codex.session", Provider, "Session").ExportingLimit("session", unit: "percent"),
        WidgetDescriptorFactories.Percent("codex.weekly", Provider, "Weekly").ExportingLimit("weekly", unit: "percent"),
        WidgetDescriptorFactories.Percent("codex.spark", Provider, "Spark").ExportingLimit("spark", unit: "percent"),
        WidgetDescriptorFactories.Percent("codex.sparkWeekly", Provider, "Spark Weekly").ExportingLimit("sparkWeekly", unit: "percent"),
        WidgetDescriptorFactories.Combined("codex.credits", Provider, "Extra Usage", metricLabel: "Credits")
            .ExportingLimit("credits", LimitResourceDescriptor.ResourceKind.Balance, "credits", new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Count, "credits"))
            .ExportingLimit("creditValue", LimitResourceDescriptor.ResourceKind.Balance, "usd", new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Dollars)),
        WidgetDescriptorFactories.Values("codex.rateLimitResets", Provider, "Rate Limit Resets", metricLabel: "Rate Limit Resets", traySuffix: "resets", showsResetExpiries: true)
            .ExportingLimit("rateLimitResets", LimitResourceDescriptor.ResourceKind.Balance, "resets", new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Count, "available")),
        WidgetDescriptorFactories.UsageTrend(Provider).ExportingHistory(UsageHistoryScope.MachineLocal, true, "From your Codex logs (estimated)")
    }.Concat(WidgetDescriptorFactories.SpendTiles(Provider)).ToList();

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var fileCandidates = AuthStore.LoadAuthCandidates();
        if (fileCandidates.Any(c => c.HasUsableAccessToken)) return true;
        var keychain = await Task.Run(() => AuthStore.LoadKeychainAuth(), cancellationToken).ConfigureAwait(false);
        return keychain?.HasUsableAccessToken == true;
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var fileCandidates = AuthStore.LoadAuthCandidates();
        Exception? lastFallbackError = null;

        foreach (var candidate in fileCandidates)
        {
            try
            {
                return await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (CodexAuthError error) when (error.AllowsAuthFallback)
            {
                lastFallbackError = error;
            }
            catch (Exception error)
            {
                return ProviderSnapshot.Error(Provider, error, _now());
            }
        }

        var keychainCandidate = await Task.Run(() => AuthStore.LoadKeychainAuth(), cancellationToken).ConfigureAwait(false);
        if (keychainCandidate is not null)
        {
            try
            {
                return await ProbeAsync(keychainCandidate, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return ProviderSnapshot.Error(Provider, error, _now());
            }
        }

        return ProviderSnapshot.Error(Provider, lastFallbackError ?? new CodexAuthError(CodexAuthErrorKind.NotLoggedIn), _now());
    }

    private async Task<ProviderSnapshot> ProbeAsync(CodexAuthState initialState, CancellationToken cancellationToken)
    {
        var authState = initialState;
        var accessToken = authState.Auth.Tokens?.AccessToken;
        if (string.IsNullOrEmpty(accessToken))
        {
            if (!string.IsNullOrEmpty(authState.Auth.ApiKey)) throw new CodexAuthError(CodexAuthErrorKind.UsageApiKey);
            throw new CodexAuthError(CodexAuthErrorKind.NotLoggedIn);
        }

        if (AuthStore.NeedsRefresh(authState.Auth))
        {
            var live = ReloadLiveAuth(authState.Source);
            if (live?.Auth.Tokens?.AccessToken is { Length: > 0 } liveToken)
            {
                authState = live;
                accessToken = liveToken;
            }
        }

        if (AuthStore.NeedsRefresh(authState.Auth) && !string.IsNullOrEmpty(authState.Auth.Tokens?.RefreshToken))
        {
            accessToken = await RefreshAccessTokenAsync(authState, authState.Auth.Tokens!.RefreshToken!).ConfigureAwait(false);
        }

        var response = await FetchUsageWithRetryAsync(accessToken!, authState).ConfigureAwait(false);
        var currentToken = authState.Auth.Tokens?.AccessToken ?? accessToken!;
        var resetCredits = await FetchResetCreditsBestEffortAsync(currentToken, authState.Auth.Tokens?.AccountId).ConfigureAwait(false);

        var body = ProviderParse.JsonObject(response.Body) ?? throw new CodexUsageError(CodexUsageErrorKind.InvalidResponse);
        var mapped = CodexUsageMapper.MapUsageResponse(response, resetCredits, _now());

        var pricing = await _pricing().ConfigureAwait(false);
        var scan = await LogUsageScanner.ScanAsync(30, _now(), pricing, cancellationToken).ConfigureAwait(false);
        ProviderUsageHistory? usageHistory = null;
        if (!cancellationToken.IsCancellationRequested && scan is not null)
        {
            const string note = "From your Codex logs (estimated)";
            usageHistory = new ProviderUsageHistory(scan.Series, scan.ModelUsage, scan.UnknownModelsByDay);
            SpendTileMapper.AppendTokenUsage(scan.Series, mapped.Lines, _now(), unknownModelsByDay: scan.UnknownModelsByDay, modelUsage: scan.ModelUsage, modelSourceNote: note);
            SpendTileMapper.AppendUsageTrend(scan.Series, mapped.Lines, _now(), note);
        }

        MetricLine.AppendNoDataIfNeeded(mapped.Lines);
        return ProviderSnapshot.Make(Provider, mapped.Plan, mapped.Lines, _now(), usageHistory);
    }

    private async Task<Services.HttpResponseResult?> FetchResetCreditsBestEffortAsync(string accessToken, string? accountId)
    {
        try
        {
            return await UsageClient.FetchResetCreditsAsync(accessToken, accountId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Plugin("codex"), $"reset-credit fetch failed; using usage-body count: {ex.Message}");
            return null;
        }
    }

    private async Task<Services.HttpResponseResult> FetchUsageWithRetryAsync(string accessToken, CodexAuthState authState)
    {
        return await ProviderAuthRetry.FetchAsync(
            accessToken,
            token => UsageClient.FetchUsageAsync(token, authState.Auth.Tokens?.AccountId),
            async () =>
            {
                var refreshToken = authState.Auth.Tokens?.RefreshToken;
                if (string.IsNullOrEmpty(refreshToken)) throw new CodexAuthError(CodexAuthErrorKind.TokenExpired);
                try
                {
                    return await RefreshAccessTokenAsync(authState, refreshToken!).ConfigureAwait(false);
                }
                catch (CodexAuthError)
                {
                    throw;
                }
                catch
                {
                    throw new CodexUsageError(CodexUsageErrorKind.ConnectionFailed);
                }
            },
            () => new CodexUsageError(CodexUsageErrorKind.ConnectionFailed),
            () => new CodexAuthError(CodexAuthErrorKind.TokenExpired)
        ).ConfigureAwait(false);
    }

    private CodexAuthState? ReloadLiveAuth(CodexAuthSource source) => source switch
    {
        CodexAuthSource.File f => AuthStore.LoadAuth(f.Path),
        CodexAuthSource.Keychain => AuthStore.LoadKeychainAuth(),
        _ => null
    };

    private async Task<string> RefreshAccessTokenAsync(CodexAuthState authState, string refreshToken)
    {
        var response = await UsageClient.RefreshTokenAsync(refreshToken).ConfigureAwait(false);
        authState.Auth.Tokens!.AccessToken = response.AccessToken;
        if (response.RefreshToken is not null) authState.Auth.Tokens.RefreshToken = response.RefreshToken;
        if (response.IdToken is not null) authState.Auth.Tokens.IdToken = response.IdToken;
        authState.Auth.LastRefresh = AIUsageISO8601.ToStringIso(_now());
        try
        {
            AuthStore.Save(authState);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogTag.AuthFor("codex"), $"failed to persist rotated credentials; using the refreshed token for this session only: {ex.Message}");
        }
        return response.AccessToken;
    }
}
