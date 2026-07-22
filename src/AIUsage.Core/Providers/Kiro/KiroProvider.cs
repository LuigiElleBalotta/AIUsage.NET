using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Kiro;

public sealed class KiroProvider : IProviderRuntime
{
    public Provider Provider { get; } = new(
        "kiro", "Kiro", "kiro",
        new List<ProviderLink>
        {
            new("Dashboard", "https://app.kiro.dev/settings/account"),
            new("Status", "https://health.aws.amazon.com/health/status")
        });

    public KiroAuthStore AuthStore { get; }
    public KiroUsageClient UsageClient { get; }
    private readonly Func<DateTimeOffset> _now;

    public KiroProvider(KiroAuthStore? authStore = null, KiroUsageClient? usageClient = null, Func<DateTimeOffset>? now = null)
    {
        AuthStore = authStore ?? new KiroAuthStore();
        UsageClient = usageClient ?? new KiroUsageClient();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public List<WidgetDescriptor> WidgetDescriptors => new()
    {
        WidgetDescriptorFactories.Percent("kiro.usage", Provider, "Usage").ExportingLimit("usage", unit: "percent"),
        WidgetDescriptorFactories.BoundedCount("kiro.requests", Provider, KiroUsageMapper.RequestsLabel, 0, "requests")
            .ExportingLimit("requests", LimitResourceDescriptor.ResourceKind.Balance, "requests", new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Count, "requests")),
        WidgetDescriptorFactories.BoundedCount("kiro.bonus", Provider, KiroUsageMapper.BonusLabel, 0, "credits"),
        WidgetDescriptorFactories.BoundedDollars("kiro.overage", Provider, "Extra Usage", 100, metricLabel: KiroUsageMapper.OverageLabel, valueWord: "spent")
            .ExportingLimit("overage", unit: "usd", source: new LimitResourceDescriptor.ResourceSource.ProgressOrValue(MetricKind.Dollars))
    };

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => AuthStore.LoadAuthCandidates().Count > 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var candidates = AuthStore.LoadAuthCandidates();
        if (candidates.Count == 0)
        {
            return ProviderSnapshot.Error(Provider, new KiroAuthError(KiroAuthErrorKind.NotLoggedIn), _now());
        }

        Exception? lastFallbackError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (KiroAuthError error) when (error.AllowsAuthFallback)
            {
                lastFallbackError = error;
            }
            catch (Exception error)
            {
                return ProviderSnapshot.Error(Provider, error, _now());
            }
        }

        return ProviderSnapshot.Error(Provider, lastFallbackError ?? new KiroAuthError(KiroAuthErrorKind.NotLoggedIn), _now());
    }

    private async Task<ProviderSnapshot> ProbeAsync(KiroAuthState authState, CancellationToken cancellationToken)
    {
        var profileArn = await EnsureProfileArnAsync(authState).ConfigureAwait(false);
        var region = KiroAuthStore.DataPlaneRegion(authState);

        var response = await ProviderAuthRetry.FetchAsync(
            authState.AccessToken,
            token => UsageClient.FetchUsageLimitsAsync(token, region, profileArn),
            () => ResolveFreshAccessTokenAsync(authState),
            () => new KiroUsageError(KiroUsageErrorKind.ConnectionFailed),
            () => new KiroAuthError(KiroAuthErrorKind.SessionExpired)
        ).ConfigureAwait(false);

        if (response.StatusCode is < 200 or >= 300) throw new KiroUsageError(KiroUsageErrorKind.RequestFailed, response.StatusCode);

        var body = ProviderParse.JsonObject(response.Body) ?? throw new KiroUsageError(KiroUsageErrorKind.InvalidResponse);
        var mapped = KiroUsageMapper.MapUsageResponse(body, _now());

        MetricLine.AppendNoDataIfNeeded(mapped.Lines);
        return ProviderSnapshot.Make(Provider, mapped.Plan, mapped.Lines, _now());
    }

    /// <summary>Resolves the CodeWhisperer profile ARN when the auth source didn't already carry
    /// one (kiro-cli caches it locally; the desktop file always has it once logged in). A missing
    /// profile isn't fatal — `getUsageLimits` can still answer without it for some account types.</summary>
    private async Task<string?> EnsureProfileArnAsync(KiroAuthState authState)
    {
        if (!string.IsNullOrEmpty(authState.ProfileArn)) return authState.ProfileArn;

        try
        {
            var region = KiroAuthStore.DataPlaneRegion(authState);
            var response = await UsageClient.ListAvailableProfilesAsync(authState.AccessToken, region).ConfigureAwait(false);
            if (response.StatusCode is < 200 or >= 300) return null;

            var body = ProviderParse.JsonObject(response.Body);
            if (body is not { } root || !root.TryGetProperty("profiles", out var profilesEl) || profilesEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }
            foreach (var profile in profilesEl.EnumerateArray())
            {
                if (profile.TryGetProperty("arn", out var arnEl) && arnEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var arn = arnEl.GetString();
                    if (!string.IsNullOrEmpty(arn))
                    {
                        authState.ProfileArn = arn;
                        return arn;
                    }
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Plugin("kiro"), $"profile ARN lookup failed; continuing without it: {ex.Message}");
            return null;
        }
    }

    /// <summary>Called only after a real 401/403 from the usage API — Kiro never refreshes
    /// proactively on a near-expiry timer. The Kiro IDE / kiro-cli own this OAuth session and rotate
    /// its single-use refresh token in the background on their own schedule; a proactive refresh here
    /// raced that rotation in practice (confirmed against real credentials: this app's own 5-minute
    /// pre-expiry refresh collided with the IDE's, AWS treated the loser as token reuse and revoked
    /// the whole family, forcing a re-login in the IDE too — see PORTING_NOTES.md). So: first re-read
    /// the live source in case the owning app already rotated it, and only fall back to calling the
    /// refresh endpoint ourselves when the on-disk token is still the one that just failed.</summary>
    private async Task<string> ResolveFreshAccessTokenAsync(KiroAuthState authState)
    {
        var staleToken = authState.AccessToken;
        var live = ReloadLiveAuth(authState.Source);
        if (live is not null && live.AccessToken != staleToken)
        {
            CopyLiveState(live, authState);
            return authState.AccessToken;
        }

        await RefreshAccessTokenAsync(authState).ConfigureAwait(false);
        return authState.AccessToken;
    }

    private KiroAuthState? ReloadLiveAuth(KiroAuthSource source) => source switch
    {
        KiroAuthSource.DesktopFile => AuthStore.LoadDesktopAuth(),
        KiroAuthSource.CliDatabase => AuthStore.LoadCliAuth(),
        _ => null
    };

    private static void CopyLiveState(KiroAuthState live, KiroAuthState target)
    {
        target.AccessToken = live.AccessToken;
        target.RefreshToken = live.RefreshToken;
        target.ProfileArn = live.ProfileArn;
        target.SsoRegion = live.SsoRegion;
        target.ExpiresAt = live.ExpiresAt;
        target.ClientId = live.ClientId;
        target.ClientSecret = live.ClientSecret;
    }

    /// <summary>Calls Kiro's own refresh endpoint. Only reached when the live source on disk still
    /// carries the same (now-rejected) access token, meaning nobody else has rotated it yet.</summary>
    private async Task RefreshAccessTokenAsync(KiroAuthState authState)
    {
        var refreshToken = authState.RefreshToken;
        if (string.IsNullOrEmpty(refreshToken)) throw new KiroAuthError(KiroAuthErrorKind.SessionExpired);

        var region = authState.SsoRegion.NilIfEmpty() ?? "us-east-1";
        KiroRefreshResponse response;
        try
        {
            response = authState.IsCliOidc
                ? await UsageClient.RefreshCliTokenAsync(refreshToken!, authState.ClientId!, authState.ClientSecret!, region).ConfigureAwait(false)
                : await UsageClient.RefreshDesktopTokenAsync(refreshToken!, region).ConfigureAwait(false);
        }
        catch (KiroAuthError)
        {
            throw;
        }
        catch
        {
            throw new KiroUsageError(KiroUsageErrorKind.ConnectionFailed);
        }

        AuthStore.SaveRefreshedToken(authState, response.AccessToken, response.RefreshToken, response.ProfileArn);
    }
}
