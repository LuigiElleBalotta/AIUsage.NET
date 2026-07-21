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
    private readonly Func<DateTimeOffset> _now;
    private const string SourceNote = "From your OpenCode logs";
    private bool _loggedAuthReadFailure;

    public OpenCodeProvider(OpenCodeAuthStore? authStore = null, OpenCodeUsageScanner? usageScanner = null, Func<DateTimeOffset>? now = null)
    {
        AuthStore = authStore ?? new OpenCodeAuthStore();
        UsageScanner = usageScanner ?? new OpenCodeUsageScanner();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public List<WidgetDescriptor> WidgetDescriptors => new List<WidgetDescriptor>
    {
        WidgetDescriptorFactories.BoundedDollars("opencode.session", Provider, "Session", OpenCodeUsageMapper.SessionCap).ExportingLimit("session", unit: "usd", estimated: true),
        WidgetDescriptorFactories.BoundedDollars("opencode.weekly", Provider, "Weekly", OpenCodeUsageMapper.WeeklyCap).ExportingLimit("weekly", unit: "usd", estimated: true),
        WidgetDescriptorFactories.BoundedDollars("opencode.monthly", Provider, "Monthly", OpenCodeUsageMapper.MonthlyCap).ExportingLimit("monthly", unit: "usd", estimated: true),
        WidgetDescriptorFactories.UsageTrend(Provider).ExportingHistory(UsageHistoryScope.MachineLocal, false, SourceNote)
    }.Concat(WidgetDescriptorFactories.SpendTiles(Provider)).ToList();

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (AuthStore.GoApiKey() is not null) return true;
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
        catch (OpenCodeUsageError error)
        {
            authReadError = error;
            if (error.Kind == OpenCodeUsageErrorKind.CredentialsUnreadable && !_loggedAuthReadFailure)
            {
                _loggedAuthReadFailure = true;
                AppLog.Warn(LogTag.Plugin("opencode"), $"auth.json unreadable: {error.Detail}");
            }
        }
        catch (Exception ex)
        {
            authReadError = new OpenCodeUsageError(OpenCodeUsageErrorKind.CredentialsUnreadable, ex.Message);
        }

        OpenCodeUsageScan? scan;
        try
        {
            scan = await UsageScanner.ScanAsync(refreshedAt, hasGoKey: hasGoKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ProviderSnapshot.Error(Provider, error, refreshedAt);
        }

        if (scan is null)
        {
            if (hasGoKey)
            {
                var windows = OpenCodeGoWindowMath.Compute(new List<(double, double)>(), null, refreshedAt);
                return ProviderSnapshot.Make(Provider, "Go", OpenCodeUsageMapper.MeterLines(windows), refreshedAt);
            }
            return ProviderSnapshot.Error(Provider, authReadError ?? new OpenCodeUsageError(OpenCodeUsageErrorKind.NotLoggedIn), refreshedAt);
        }

        var lines = new List<MetricLine>();
        if (scan.GoWindows is { } w) lines.AddRange(OpenCodeUsageMapper.MeterLines(w));

        SpendTileMapper.AppendTokenUsage(scan.LogScan.Series, lines, refreshedAt, estimated: false,
            unknownModelsByDay: scan.LogScan.UnknownModelsByDay, modelUsage: scan.LogScan.ModelUsage, modelSourceNote: SourceNote);
        SpendTileMapper.AppendUsageTrend(scan.LogScan.Series, lines, refreshedAt, SourceNote);
        MetricLine.AppendNoDataIfNeeded(lines);

        var plan = scan.GoWindows is not null ? "Go" : null;
        var usageHistory = new ProviderUsageHistory(scan.LogScan.Series, scan.LogScan.ModelUsage, scan.LogScan.UnknownModelsByDay);
        return ProviderSnapshot.Make(Provider, plan, lines, refreshedAt, usageHistory);
    }
}
