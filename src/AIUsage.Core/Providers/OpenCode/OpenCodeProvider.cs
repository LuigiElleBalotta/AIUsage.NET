using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

public sealed class OpenCodeProvider : IProviderRuntime
{
    public Provider Provider { get; } = new(
        "opencode", "OpenCode", "opencode",
        new List<ProviderLink> { new("Dashboard", "https://opencode.ai/auth") });

    public OpenCodeAuthStore AuthStore { get; }
    public OpenCodeUsageScanner UsageScanner { get; }
    private readonly OpenCodeGoStatusClient _quotaClient;
    private readonly Func<DateTimeOffset> _now;
    private const string SourceNote = OpenCodeUsageMapper.QuotaSourceNote;
    private const string PartialCostSourceNote = "Some messages have no recorded cost, so dollar totals are known subtotals.";
    private const string PartialCostBadgeSubtitle = "Some OpenCode messages have no recorded cost. Dollar figures are known subtotals; missing costs aren't guessed.";
    private bool _loggedAuthReadFailure;
    private string? _lastCredentialIssue;

    public OpenCodeProvider(
        OpenCodeAuthStore? authStore = null,
        OpenCodeUsageScanner? usageScanner = null,
        Func<DateTimeOffset>? now = null,
        OpenCodeGoStatusClient? quotaClient = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        AuthStore = authStore ?? new OpenCodeAuthStore(now: _now);
        UsageScanner = usageScanner ?? new OpenCodeUsageScanner(versionProbe: new OpenCodeVersionProbe());
        _quotaClient = quotaClient ?? new OpenCodeGoStatusClient();
    }

    public List<WidgetDescriptor> WidgetDescriptors => new List<WidgetDescriptor>
    {
        // The sample is only a layout placeholder; every rendered number and limit comes from the
        // authoritative Console quota (or the meter is omitted). No hardcoded plan fallback. These
        // three widgets display the server quota as percent left, while the raw MetricLine and the
        // machine-facing limits API keep the server's native USD amounts.
        WidgetDescriptorFactories.Percent("opencode.session", Provider, "Session").ExportingLimit("session", unit: "usd", estimated: false).ShowingRemainingPercent(),
        WidgetDescriptorFactories.Percent("opencode.weekly", Provider, "Weekly").ExportingLimit("weekly", unit: "usd", estimated: false).ShowingRemainingPercent(),
        WidgetDescriptorFactories.Percent("opencode.monthly", Provider, "Monthly").ExportingLimit("monthly", unit: "usd", estimated: false).ShowingRemainingPercent(),
        WidgetDescriptorFactories.UsageTrend(Provider).ExportingHistory(UsageHistoryScope.MachineLocal, false, SourceNote)
    }.Concat(WidgetDescriptorFactories.SpendTiles(Provider)).ToList();

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (AuthStore.HasAnyLocalCredential()) return true;
            }
            catch
            {
                return true;
            }
            return UsageScanner.HasHostedUsage();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var refreshedAt = _now();

        var hasGoKey = false;
        OpenCodeUsageError? authReadError = null;
        try
        {
            hasGoKey = await Task.Run(() => AuthStore.GoApiKey() is not null, cancellationToken).ConfigureAwait(false);
            _loggedAuthReadFailure = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OpenCodeUsageError error)
        {
            authReadError = error;
            LogAuthReadFailure(error);
        }
        catch (Exception ex)
        {
            authReadError = new OpenCodeUsageError(OpenCodeUsageErrorKind.CredentialsUnreadable, ex.Message);
            LogAuthReadFailure(authReadError);
        }

        OpenCodeCredentialState credentialState;
        try
        {
            credentialState = await Task.Run(AuthStore.LoadConsoleCredential, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            credentialState = new OpenCodeCredentialState(OpenCodeCredentialStatus.Unreadable, Detail: ex.Message);
            AppLog.Warn(LogTag.Plugin("opencode"), $"could not load Console credential: {ex.Message}");
        }

        var issue = credentialState.Status is OpenCodeCredentialStatus.Invalid
            or OpenCodeCredentialStatus.UntrustedOrigin
            or OpenCodeCredentialStatus.Unreadable
            ? $"{credentialState.Status}: {credentialState.Detail ?? "no detail"}"
            : null;
        if (issue is not null && issue != _lastCredentialIssue)
        {
            AppLog.Warn(LogTag.Plugin("opencode"), $"Console credential unusable: {issue}");
        }
        _lastCredentialIssue = issue;

        OpenCodeUsageScan? scan = null;
        OpenCodeUsageError? localError = null;
        try
        {
            scan = await UsageScanner.ScanAsync(refreshedAt, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OpenCodeUsageError error)
        {
            localError = error;
            LogLocalUsageFailure(error);
        }
        catch (Exception ex)
        {
            localError = new OpenCodeUsageError(OpenCodeUsageErrorKind.DatabaseUnreadable, ex.Message);
            LogLocalUsageFailure(localError);
        }

        if (scan is null && localError is null && authReadError is null && credentialState.Status == OpenCodeCredentialStatus.None && !hasGoKey)
        {
            return ProviderSnapshot.Error(
                Provider,
                new OpenCodeUsageError(OpenCodeUsageErrorKind.NotLoggedIn),
                refreshedAt);
        }

        var lines = new List<MetricLine>();
        OpenCodeGoStatus? quota = null;
        MetricLine? quotaBadge = null;
        string? quotaLimitsError = null;
        var hasLocalUsage = scan is not null && scan.LogScan.Series.Daily.Count > 0;
        var localSuffix = hasLocalUsage ? " Local usage is still shown." : "";

        if (credentialState.IsUsable)
        {
            try
            {
                quota = await _quotaClient.FetchAsync(credentialState.Credential!, cancellationToken).ConfigureAwait(false);
                if (quota.HasAccess)
                {
                    lines.AddRange(OpenCodeUsageMapper.MeterLines(quota));
                }
                else
                {
                    quotaBadge = OpenCodeUsageMapper.QuotaBadge(
                        "No Active Go", "#A3A3A3",
                        "OpenCode Console reports no active Go subscription." + localSuffix);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OpenCodeQuotaError error)
            {
                AppLog.Warn(LogTag.Plugin("opencode"), $"Console quota unavailable: {error.Kind} ({error.StatusCode?.ToString() ?? "no status"})");
                quotaBadge = QuotaBadgeFor(error, localSuffix);
            }
            catch (Exception ex)
            {
                AppLog.Error(LogTag.Plugin("opencode"), $"Console quota failed: {ex.Message}");
                quotaBadge = OpenCodeUsageMapper.QuotaBadge(
                    "Unavailable", "#EF4444",
                    "OpenCode Console quota is unavailable." + localSuffix);
            }
        }
        else
        {
            quotaBadge = credentialState.Status switch
            {
                OpenCodeCredentialStatus.Expired => OpenCodeUsageMapper.QuotaBadge(
                    "Reconnect", "#F59E0B",
                    "OpenCode sign-in expired. Run OpenCode to reconnect." + localSuffix),
                OpenCodeCredentialStatus.Invalid => OpenCodeUsageMapper.QuotaBadge(
                    "Unavailable", "#EF4444",
                    "OpenCode's saved credential is invalid. Run OpenCode to reconnect." + localSuffix),
                OpenCodeCredentialStatus.UntrustedOrigin => OpenCodeUsageMapper.QuotaBadge(
                    "Unavailable", "#EF4444",
                    "The saved credential points to an unsupported server." + localSuffix),
                OpenCodeCredentialStatus.Unreadable => OpenCodeUsageMapper.QuotaBadge(
                    "Unavailable", "#F59E0B",
                    "Couldn't read the saved OpenCode credential." + localSuffix),
                _ when hasGoKey || scan?.Report.HasGoUsage == true => OpenCodeUsageMapper.QuotaBadge(
                    "Unavailable", "#A3A3A3",
                    "Server quotas need a current OpenCode Console sign-in." + localSuffix),
                _ => null
            };
        }

        if (quotaBadge is MetricLine.Badge quotaStatus)
        {
            lines.Add(quotaStatus);
            // "No Active Go" is a successful answer (no subscription), not a request/credential
            // failure, so it never becomes a machine-facing limits error.
            if (quota is not { HasAccess: false })
            {
                quotaLimitsError = quotaStatus.Subtitle ?? quotaStatus.BadgeText;
            }
        }

        if (localError is not null)
        {
            var suffix = quota?.HasAccess == true ? " Server quota is still shown." : "";
            lines.Add(OpenCodeUsageMapper.LocalUsageBadge(
                "Unavailable", "#EF4444",
                localError.Message + suffix));
        }

        if (scan is not null)
        {
            var localLines = new List<MetricLine>();
            try
            {
                // The scanner returns the calendar-now it used for its local day buckets; using the same
                // value here keeps Today/Yesterday/the trend aligned with those buckets, including DST.
                var calendarNow = scan.CalendarNow;
                // Only a mixed history has a misleading subtotal; when every row lacks a cost the
                // spend tiles stay token-only and there is no dollar figure to qualify.
                var partialCost = scan.Report.HasMissingCost
                    && scan.LogScan.Series.Daily.Any(d => d.CostUSD.HasValue);
                var spendSourceNote = partialCost ? $"{SourceNote} {PartialCostSourceNote}" : SourceNote;
                SpendTileMapper.AppendTokenUsage(
                    scan.LogScan.Series,
                    localLines,
                    calendarNow,
                    estimated: false,
                    unknownModelsByDay: scan.LogScan.UnknownModelsByDay,
                    modelUsage: scan.LogScan.ModelUsage,
                    modelSourceNote: spendSourceNote);
                SpendTileMapper.AppendUsageTrend(scan.LogScan.Series, localLines, calendarNow, SourceNote);

                if (scan.Report.UnsupportedDatabaseCount > 0)
                {
                    lines.Add(OpenCodeUsageMapper.LocalUsageBadge(
                        "Unsupported Schema", "#EF4444",
                        "OpenCode's local database format isn't recognized. Update OpenCode or AIUsage.NET."));
                }
                if (scan.Report.FailedDatabaseCount > 0)
                {
                    lines.Add(OpenCodeUsageMapper.LocalUsageBadge(
                        "Partial Data", "#F59E0B",
                        "Some OpenCode databases couldn't be read this refresh."));
                }
                if (partialCost)
                {
                    lines.Add(OpenCodeUsageMapper.LocalUsageBadge(
                        "Partial Cost", "#F59E0B", PartialCostBadgeSubtitle));
                }
                lines.AddRange(localLines);
            }
            catch (Exception ex)
            {
                scan = null;
                localError = new OpenCodeUsageError(OpenCodeUsageErrorKind.DatabaseUnreadable, ex.Message);
                LogLocalUsageFailure(localError);
                var suffix = quota?.HasAccess == true ? " Server quota is still shown." : "";
                lines.Add(OpenCodeUsageMapper.LocalUsageBadge(
                    "Unavailable", "#EF4444",
                    localError.Message + suffix));
            }
        }

        MetricLine.AppendNoDataIfNeeded(lines);

        var plan = quota is { HasAccess: true } ? OpenCodeUsageMapper.PlanName(quota)
            : quota is { HasAccess: false } ? null
            : hasGoKey || scan?.Report.HasGoUsage == true ? "Go" : null;
        var usageHistory = scan is null
            ? null
            : new ProviderUsageHistory(scan.LogScan.Series, scan.LogScan.ModelUsage, scan.LogScan.UnknownModelsByDay);
        return ProviderSnapshot.Make(Provider, plan, lines, refreshedAt, usageHistory)
            with
            {
                LimitsError = quotaLimitsError,
                // Provenance for the persisted cache: every nonfatal current snapshot (authoritative
                // Console quota, No Active Go, quota error, or Zen-only local usage) is served by the
                // current semantics, so older inferred-quota caches can be told apart after upgrade.
                DataVersion = OpenCodeQuotaCompatibility.CurrentDataVersion
            };
    }

    private static MetricLine QuotaBadgeFor(OpenCodeQuotaError error, string localSuffix) => error.Kind switch
    {
        OpenCodeQuotaErrorKind.AuthRejected => OpenCodeUsageMapper.QuotaBadge(
            "Reconnect", "#EF4444",
            "OpenCode Console rejected the saved sign-in. Run OpenCode to reconnect." + localSuffix),
        OpenCodeQuotaErrorKind.Timeout or OpenCodeQuotaErrorKind.ConnectionFailed => OpenCodeUsageMapper.QuotaBadge(
            "Unavailable", "#F59E0B",
            "Couldn't reach OpenCode Console." + localSuffix),
        OpenCodeQuotaErrorKind.RedirectRejected or OpenCodeQuotaErrorKind.UntrustedOrigin => OpenCodeUsageMapper.QuotaBadge(
            "Unavailable", "#EF4444",
            "OpenCode Console redirected the request or the credential origin is unsupported; it wasn't followed." + localSuffix),
        OpenCodeQuotaErrorKind.InvalidCredential => OpenCodeUsageMapper.QuotaBadge(
            "Reconnect", "#EF4444",
            "OpenCode's saved credential is invalid. Run OpenCode to reconnect." + localSuffix),
        _ => OpenCodeUsageMapper.QuotaBadge(
            "Unavailable", "#EF4444",
            "OpenCode Console returned an unexpected quota response." + localSuffix)
    };

    private void LogAuthReadFailure(OpenCodeUsageError error)
    {
        if (_loggedAuthReadFailure) return;
        _loggedAuthReadFailure = true;
        AppLog.Warn(LogTag.Plugin("opencode"), $"credentials unreadable: {error.Detail}");
    }

    private static void LogLocalUsageFailure(OpenCodeUsageError error) =>
        AppLog.Warn(LogTag.Plugin("opencode"),
            $"local usage unavailable: {error.Kind} ({error.Detail ?? "no detail"})");
}
