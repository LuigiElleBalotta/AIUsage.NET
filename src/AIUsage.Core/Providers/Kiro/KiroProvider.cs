using System.Security.Cryptography;
using System.Text;
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

    /// <summary>Fingerprint (SHA-256, never the raw token) of the last refresh token AWS actually
    /// rejected over the network, per credential source. Once set, a refresh attempt carrying the
    /// same fingerprint fails immediately instead of hitting the network again — repeatedly retrying
    /// a refresh token AWS already rejected (once every 5-minute poll, indefinitely) was observed to
    /// eventually invalidate the live Kiro IDE session too, not just this app's cached copy (see
    /// PORTING_NOTES.md). In-memory only: a fresh process (e.g. after the user signs in again) always
    /// gets a clean slate, and a token rotated by the owning app on disk produces a new fingerprint
    /// that is retried normally.</summary>
    private readonly Dictionary<string, string> _knownDeadRefreshTokens = new();

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
    /// refresh endpoint ourselves when the on-disk token is still the one that just failed — and even
    /// then, never retry a refresh token AWS has already told us is dead (see
    /// <see cref="_knownDeadRefreshTokens"/>; repeatedly retrying it every 5-minute poll was observed
    /// to eventually take down the live IDE session too, not just this app's copy).</summary>
    private async Task<string> ResolveFreshAccessTokenAsync(KiroAuthState authState)
    {
        var staleToken = authState.AccessToken;
        var live = ReloadLiveAuth(authState.Source);
        if (live is not null && live.AccessToken != staleToken)
        {
            CopyLiveState(live, authState);
            return authState.AccessToken;
        }

        var sourceKey = SourceKey(authState.Source);
        if (!string.IsNullOrEmpty(authState.RefreshToken))
        {
            var fingerprint = Fingerprint(authState.RefreshToken!);
            if (_knownDeadRefreshTokens.TryGetValue(sourceKey, out var deadFingerprint) && deadFingerprint == fingerprint)
            {
                AppLog.Warn(LogTag.Plugin("kiro"), "refresh token already known to be rejected by AWS; not retrying over the network");
                throw new KiroAuthError(KiroAuthErrorKind.SessionExpired);
            }
        }

        var attemptedToken = authState.RefreshToken;
        try
        {
            await RefreshAccessTokenAsync(authState).ConfigureAwait(false);
        }
        catch (KiroAuthError)
        {
            // AWS explicitly rejected this refresh token (a KiroAuthError here means the refresh
            // endpoint answered with a real rejection — non-2xx or an unparseable payload; a
            // transport/connection failure throws KiroUsageError instead and is retried normally
            // next cycle). Remember the fingerprint so the next refresh cycle doesn't hit the network
            // with the same dead token again.
            if (attemptedToken is { Length: > 0 })
            {
                _knownDeadRefreshTokens[sourceKey] = Fingerprint(attemptedToken);
            }
            throw;
        }
        return authState.AccessToken;
    }

    private static string SourceKey(KiroAuthSource source) => source switch
    {
        KiroAuthSource.DesktopFile f => $"desktop:{f.Path}",
        KiroAuthSource.CliDatabase db => $"cli:{db.Path}:{db.TokenKey}",
        _ => "unknown"
    };

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
