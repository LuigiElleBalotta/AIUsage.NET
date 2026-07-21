using AIUsage.Core.Models;
using AIUsage.Core.Pricing;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Claude;

public sealed class ClaudeProvider : IProviderRuntime
{
    public Provider Provider { get; }
    public ClaudeAuthStore AuthStore { get; }
    public ClaudeDesktopAuthStore DesktopAuthStore { get; }
    public ClaudeUsageClient UsageClient { get; }
    public ClaudeLogUsageScanner LogUsageScanner { get; }
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Task<ModelPricing>> _pricing;

    private byte[]? _cachedCredentialFingerprint;
    private ClaudeMappedUsage? _lastGoodUsage;
    private DateTimeOffset? _rateLimitedUntil;
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);

    public ClaudeProvider(
        Provider? provider = null,
        ClaudeAuthStore? authStore = null,
        ClaudeDesktopAuthStore? desktopAuthStore = null,
        ClaudeUsageClient? usageClient = null,
        ClaudeLogUsageScanner? logUsageScanner = null,
        Func<DateTimeOffset>? now = null,
        Func<Task<ModelPricing>>? pricing = null)
    {
        Provider = provider ?? MakeProvider();
        AuthStore = authStore ?? new ClaudeAuthStore();
        DesktopAuthStore = desktopAuthStore ?? new ClaudeDesktopAuthStore();
        UsageClient = usageClient ?? new ClaudeUsageClient();
        LogUsageScanner = logUsageScanner ?? new ClaudeLogUsageScanner();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pricing = pricing ?? (() => Task.FromResult(ModelPricingStore.Shared.Current()));
    }

    public static Provider MakeProvider(string id = "claude", string displayName = "Claude") => new(
        id, displayName, "claude",
        new List<ProviderLink>
        {
            new("Status", "https://status.anthropic.com/"),
            new("Dashboard", "https://claude.ai/settings/usage")
        });

    public List<WidgetDescriptor> WidgetDescriptors => new List<WidgetDescriptor>
    {
        WidgetDescriptorFactories.Percent($"{Provider.Id}.session", Provider, "Session", isSessionWindow: true)
            .ExportingLimit("session", unit: "percent"),
        WidgetDescriptorFactories.Percent($"{Provider.Id}.weekly", Provider, "Weekly")
            .ExportingLimit("weekly", unit: "percent"),
        WidgetDescriptorFactories.Percent($"{Provider.Id}.sonnet", Provider, "Sonnet")
            .ExportingLimit("sonnet", unit: "percent"),
        WidgetDescriptorFactories.Percent($"{Provider.Id}.fable", Provider, "Fable")
            .ExportingLimit("fable", unit: "percent"),
        WidgetDescriptorFactories.BoundedDollars($"{Provider.Id}.extra", Provider, "Extra Usage", 100, metricLabel: "Extra usage spent", valueWord: "spent")
            .ExportingLimit("extraUsage", unit: "usd", source: new LimitResourceDescriptor.ResourceSource.ProgressOrValue(MetricKind.Dollars)),
        WidgetDescriptorFactories.UsageTrend(Provider)
            .ExportingHistory(UsageHistoryScope.MachineLocal, true, "From your Claude usage history (estimated)")
    }.Concat(WidgetDescriptorFactories.SpendTiles(Provider)).ToList();

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var candidates = AuthStore.LoadCredentialCandidates();
            if (candidates.Any(c => c.HasUsableAccessToken)) return true;
            // No Claude Code CLI credentials found — a Claude Desktop install with cached OAuth
            // material still counts as "usable locally" for first-run provider detection.
            return DesktopAuthStore.HasCredentialMaterial();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var storedCandidates = await Task.Run(() => AuthStore.LoadCredentialCandidates(), cancellationToken).ConfigureAwait(false);
        var candidates = storedCandidates.Where(c => c.HasUsableAccessToken).ToList();

        if (candidates.Count == 0)
        {
            // Claude Code's own CLI credentials (file/Credential Manager) weren't found — fall back to
            // borrowing a currently-valid access token from a local Claude Desktop install, if any.
            var desktopState = await Task.Run(() => DesktopAuthStore.Load(), cancellationToken).ConfigureAwait(false);
            if (desktopState is not null)
            {
                AppLog.Info(LogTag.AuthFor("claude"), "no Claude Code credentials found; using Claude Desktop fallback");
                candidates.Add(desktopState);
            }
        }

        if (candidates.Count == 0)
        {
            AppLog.Info(LogTag.AuthFor("claude"), "no access token, not logged in");
            return ProviderSnapshot.Error(Provider, new ClaudeAuthError(ClaudeAuthErrorKind.NotLoggedIn), _now());
        }

        var sources = string.Join(", ", candidates.Select(c => c.DiagnosticsLabel(_now())));
        AppLog.Info(LogTag.Plugin("claude"), $"refresh start ({candidates.Count} source{(candidates.Count == 1 ? "" : "s")}: {sources})");

        Exception? lastFallbackError = null;
        foreach (var state in candidates)
        {
            try
            {
                return await ProbeAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (ClaudeAuthError error) when (error.AllowsAuthFallback)
            {
                AppLog.Warn(LogTag.AuthFor("claude"), $"{state.Source.Label} failed ({error.Message}); falling back to next source if any");
                lastFallbackError = error;
            }
            catch (Exception error)
            {
                return ProviderSnapshot.Error(Provider, error, _now());
            }
        }

        return ProviderSnapshot.Error(Provider, lastFallbackError ?? new ClaudeAuthError(ClaudeAuthErrorKind.NotLoggedIn), _now());
    }

    private async Task<ProviderSnapshot> ProbeAsync(ClaudeCredentialState initialState, CancellationToken cancellationToken)
    {
        var state = initialState;
        var mapped = new ClaudeMappedUsage
        {
            Plan = ClaudeUsageMapper.FormatPlan(state.OAuth.SubscriptionType, state.OAuth.RateLimitTier)
        };

        string? warning = null;
        switch (AuthStore.LiveUsageAvailability(state))
        {
            case ClaudeLiveUsageAvailability.Available:
                mapped = await FetchLiveUsageAsync(state).ConfigureAwait(false);
                warning = mapped.Warning;
                break;
            case ClaudeLiveUsageAvailability.MissingProfileScope:
                AppLog.Warn(LogTag.Plugin("claude"), "live usage unavailable: credential lacks the user:profile scope; re-login with `claude` to restore session/weekly limits");
                warning = ClaudeUsageMapper.MissingProfileScopeWarning;
                break;
            case ClaudeLiveUsageAvailability.InferenceOnlyToken:
                break;
        }

        var pricing = await _pricing().ConfigureAwait(false);
        var scan = await LogUsageScanner.ScanAsync(30, _now(), pricing, cancellationToken).ConfigureAwait(false);
        ProviderUsageHistory? usageHistory = null;
        if (!cancellationToken.IsCancellationRequested && scan is not null)
        {
            const string note = "From your Claude usage history (estimated)";
            usageHistory = new ProviderUsageHistory(scan.Series, scan.ModelUsage, scan.UnknownModelsByDay);
            SpendTileMapper.AppendTokenUsage(scan.Series, mapped.Lines, _now(), unknownModelsByDay: scan.UnknownModelsByDay, modelUsage: scan.ModelUsage, modelSourceNote: note);
            SpendTileMapper.AppendUsageTrend(scan.Series, mapped.Lines, _now(), note);
        }

        MetricLine.AppendNoDataIfNeeded(mapped.Lines);
        return ProviderSnapshot.Make(Provider, mapped.Plan, mapped.Lines, _now(), usageHistory, warning);
    }

    private async Task<ClaudeMappedUsage> FetchLiveUsageAsync(ClaudeCredentialState state)
    {
        ActivateLiveUsageCache(state.OAuth);

        if (_rateLimitedUntil is { } until && _now() < until)
        {
            return RateLimitedSnapshot(state.OAuth, (int)Math.Ceiling((until - _now()).TotalSeconds));
        }

        var config = AuthStore.OAuthConfig();
        if (AuthStore.NeedsRefresh(state.OAuth) && !string.IsNullOrEmpty(state.OAuth.RefreshToken))
        {
            await RefreshAccessTokenAsync(state, state.OAuth.RefreshToken!, config).ConfigureAwait(false);
        }

        var response = await ProviderAuthRetry.FetchAsync(
            state.OAuth.AccessToken ?? "",
            token => UsageClient.FetchUsageAsync(token, config),
            async () =>
            {
                if (string.IsNullOrEmpty(state.OAuth.RefreshToken)) throw new ClaudeAuthError(ClaudeAuthErrorKind.TokenExpired);
                return await RefreshAccessTokenAsync(state, state.OAuth.RefreshToken!, config).ConfigureAwait(false);
            },
            () => new ClaudeUsageError(ClaudeUsageErrorKind.ConnectionFailed),
            () => new ClaudeAuthError(ClaudeAuthErrorKind.TokenExpired)
        ).ConfigureAwait(false);

        if (response.StatusCode == 429)
        {
            var retryAfterSeconds = ClaudeUsageMapper.ParseRetryAfterSeconds(response, _now());
            _rateLimitedUntil = _now().AddSeconds(retryAfterSeconds ?? (int)RateLimitCooldown.TotalSeconds);
            return RateLimitedSnapshot(state.OAuth, retryAfterSeconds);
        }

        var mapped = ClaudeUsageMapper.MapUsageResponse(response, state.OAuth, _now());
        _lastGoodUsage = mapped;
        _rateLimitedUntil = null;
        return mapped;
    }

    private ClaudeMappedUsage RateLimitedSnapshot(ClaudeOAuth credentials, int? retryAfterSeconds)
    {
        if (_lastGoodUsage is null)
        {
            return ClaudeUsageMapper.RateLimitedUsage(credentials, retryAfterSeconds);
        }
        var mapped = new ClaudeMappedUsage { Plan = _lastGoodUsage.Plan, Lines = new List<MetricLine>(_lastGoodUsage.Lines) };
        mapped.Lines.Add(ClaudeUsageMapper.RateLimitedNote(retryAfterSeconds));
        mapped.Warning = ClaudeUsageMapper.RateLimitedWarning(retryAfterSeconds);
        return mapped;
    }

    private void ActivateLiveUsageCache(ClaudeOAuth credentials)
    {
        var fingerprint = CredentialFingerprint(credentials);
        if (_cachedCredentialFingerprint is not null && _cachedCredentialFingerprint.SequenceEqual(fingerprint)) return;
        _cachedCredentialFingerprint = fingerprint;
        _lastGoodUsage = null;
        _rateLimitedUntil = null;
    }

    private static byte[] CredentialFingerprint(ClaudeOAuth credentials)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var access = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(credentials.AccessToken ?? ""));
        var refresh = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(credentials.RefreshToken ?? ""));
        return sha.ComputeHash(access.Concat(refresh).ToArray());
    }

    private async Task<string> RefreshAccessTokenAsync(ClaudeCredentialState state, string refreshToken, ClaudeOAuthConfig config)
    {
        AppLog.Info(LogTag.AuthFor("claude"), "token refresh attempt");
        var response = await UsageClient.RefreshTokenAsync(refreshToken, config).ConfigureAwait(false);
        if (response.StatusCode == 400 || response.StatusCode == 401)
        {
            var body = ProviderParse.JsonObject(response.Body);
            string? errorCode = null;
            if (body is { } b)
            {
                if (b.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String) errorCode = e.GetString();
                else if (b.TryGetProperty("error_description", out var ed) && ed.ValueKind == System.Text.Json.JsonValueKind.String) errorCode = ed.GetString();
            }
            if (errorCode == "invalid_grant")
            {
                AppLog.Warn(LogTag.AuthFor("claude"), "session expired (invalid_grant)");
                throw new ClaudeAuthError(ClaudeAuthErrorKind.SessionExpired);
            }
            throw new ClaudeUsageError(ClaudeUsageErrorKind.RequestFailed, response.StatusCode);
        }
        if (response.StatusCode is < 200 or >= 300) throw new ClaudeUsageError(ClaudeUsageErrorKind.RequestFailed, response.StatusCode);

        var decoded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(response.Body, Support.JsonDefaults.Options)
                      ?? throw new ClaudeUsageError(ClaudeUsageErrorKind.InvalidResponse);
        var doc = System.Text.Json.JsonDocument.Parse(response.Body).RootElement;
        var accessToken = doc.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(accessToken)) throw new ClaudeUsageError(ClaudeUsageErrorKind.InvalidResponse);

        var previousFingerprint = CredentialFingerprint(state.OAuth);
        state.OAuth.AccessToken = accessToken;
        if (doc.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            state.OAuth.RefreshToken = rt.GetString();
        }
        if (doc.TryGetProperty("expires_in", out var ei) && ei.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            state.OAuth.ExpiresAt = _now().ToUnixTimeMilliseconds() + ei.GetDouble() * 1000;
        }

        try
        {
            await Task.Run(() => AuthStore.Save(state)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogTag.AuthFor("claude"), $"failed to persist rotated credentials; using the refreshed token for this session only: {ex.Message}");
        }
        if (_cachedCredentialFingerprint is not null && _cachedCredentialFingerprint.SequenceEqual(previousFingerprint))
        {
            _cachedCredentialFingerprint = CredentialFingerprint(state.OAuth);
        }
        AppLog.Info(LogTag.AuthFor("claude"), "token refresh ok (rotated)");
        return accessToken!;
    }
}
