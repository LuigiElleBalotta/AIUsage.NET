using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Support;

namespace AIUsage.Core.Stores;

public sealed record StalenessHint(string Label, string Tooltip);

public enum RefreshOutcome { Refreshed, Failed, CacheHit, Skipped, BackedOff }

/// <summary>
/// Owns the latest snapshot per provider, the refresh loop, and WidgetData resolution. Simplified port
/// of the Swift WidgetDataStore: keeps the refresh/cache/backoff machinery and the MetricLine ->
/// WidgetData resolution (the parts every UI surface depends on), but omits iCloud peer-history
/// aggregation, quota-pace notifications, and telemetry hooks — none of which exist yet on this
/// platform (see PORTING_NOTES.md). Those can be layered back in once ported.
/// </summary>
public sealed class WidgetDataStore
{
    private readonly WidgetRegistry _registry;
    private readonly Dictionary<string, IProviderRuntime> _providersById;
    private readonly ProviderSnapshotCache _cache;
    private readonly Func<string, bool> _isProviderEnabled;
    private readonly Func<DateTimeOffset> _now;
    private static readonly TimeSpan FailureRetryBackoff = TimeSpan.FromSeconds(60);

    /// <summary>The underlying persisted snapshot cache, exposed so App-layer features (usage
    /// history export/import) can read/write it directly without re-plumbing a second reference.</summary>
    public ProviderSnapshotCache Cache => _cache;

    public Dictionary<string, ProviderSnapshot> Snapshots { get; private set; } = new();
    public HashSet<string> RefreshingProviderIds { get; } = new();
    public DateTimeOffset? LastRefreshAt { get; private set; }
    public Dictionary<string, string> ProviderErrors { get; } = new();

    private readonly Dictionary<string, DateTimeOffset> _failureRetryAfter = new();

    // RefreshAllAsync fans out one RefreshAsync task per provider (Task.WhenAll), so every mutation of
    // Snapshots/ProviderErrors/_failureRetryAfter below must be serialized — these are plain
    // Dictionary<> instances, not concurrent collections, and concurrent writes from parallel provider
    // refreshes corrupted their internal state ("A concurrent update was performed on this collection").
    private readonly object _mutationLock = new();

    public WidgetDisplayMode MeterStyle { get; set; } = WidgetDisplayMode.Remaining;
    public Support.ResetDisplayMode ResetDisplayMode { get; set; } = Support.ResetDisplayMode.Relative;
    public bool AlwaysShowPacing { get; set; }

    public WidgetDataStore(
        WidgetRegistry registry,
        List<IProviderRuntime> providers,
        ProviderSnapshotCache? cache = null,
        Func<string, bool>? isProviderEnabled = null,
        Func<DateTimeOffset>? now = null)
    {
        _registry = registry;
        _providersById = providers.ToDictionary(p => p.Provider.Id);
        _cache = cache ?? new ProviderSnapshotCache();
        _isProviderEnabled = isProviderEnabled ?? (_ => true);
        _now = now ?? (() => DateTimeOffset.UtcNow);

        // Stale-while-revalidate: load whatever was cached (expired included) so the tray/dashboard
        // show last-known values immediately at launch instead of blank, while the refresh loop
        // replaces them as soon as fresh data lands.
        Snapshots = _cache.LoadSnapshots(_registry.Providers.Select(p => p.Id));
    }

    public async Task RefreshAllAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var providerIds = _registry.Providers.Select(p => p.Id).Where(_isProviderEnabled).ToList();
        AppLog.Info(LogTag.Refresh, $"batch start ({providerIds.Count} providers, force={force})");
        var tasks = providerIds.Select(id => RefreshAsync(id, force, cancellationToken)).ToList();
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        LastRefreshAt = DateTimeOffset.UtcNow;
        var refreshed = outcomes.Count(o => o == RefreshOutcome.Refreshed);
        var failed = outcomes.Count(o => o == RefreshOutcome.Failed);
        var cached = outcomes.Count(o => o == RefreshOutcome.CacheHit);
        var backedOff = outcomes.Count(o => o == RefreshOutcome.BackedOff);
        AppLog.Info(LogTag.Refresh, $"batch end ({refreshed} ok / {failed} failed / {cached} cached / {backedOff} backed off)");
    }

    public async Task<RefreshOutcome> RefreshAsync(string providerId, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_isProviderEnabled(providerId)) return RefreshOutcome.Skipped;

        if (!force && _cache.Snapshot(providerId) is { } cached)
        {
            AppLog.Debug(LogTag.Refresh, $"cache hit {providerId}");
            lock (_mutationLock)
            {
                if (!Snapshots.TryGetValue(providerId, out var existing) || existing != cached)
                {
                    Snapshots[providerId] = cached;
                }
            }
            return RefreshOutcome.CacheHit;
        }

        bool backedOff;
        lock (_mutationLock)
        {
            backedOff = !force && _failureRetryAfter.TryGetValue(providerId, out var retryAfter) && _now() < retryAfter;
        }
        if (backedOff)
        {
            AppLog.Debug(LogTag.Refresh, $"backoff skip {providerId}");
            return RefreshOutcome.BackedOff;
        }

        if (!_providersById.TryGetValue(providerId, out var provider)) return RefreshOutcome.Skipped;

        lock (RefreshingProviderIds)
        {
            if (RefreshingProviderIds.Contains(providerId)) return RefreshOutcome.Skipped;
            RefreshingProviderIds.Add(providerId);
        }
        try
        {
            var snapshot = await provider.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                AppLog.Debug(LogTag.Refresh, $"cancelled {providerId} refresh; keeping last-good snapshot");
                return RefreshOutcome.Skipped;
            }

            if (ErrorMessage(snapshot) is { } message)
            {
                lock (_mutationLock)
                {
                    ProviderErrors[providerId] = message;
                    _failureRetryAfter[providerId] = _now() + FailureRetryBackoff;
                }
                AppLog.Warn(LogTag.Refresh, $"{providerId} failed: {message}");
                return RefreshOutcome.Failed;
            }

            lock (_mutationLock)
            {
                ProviderErrors.Remove(providerId);
                _failureRetryAfter.Remove(providerId);

                // Defensive use-boundary guard: even a provider path that forgot to stamp the current
                // quota semantics cannot put incompatible meters into the live snapshot store.
                snapshot = OpenCodeQuotaCompatibility.Normalize(snapshot);

                // Preserve the last-good normalized history when this pass's scan produced none (a
                // live limits fetch can succeed while an optional local log/CSV scan finds nothing new).
                if (snapshot.UsageHistory is null && Snapshots.TryGetValue(providerId, out var previous) && previous.UsageHistory is not null)
                {
                    snapshot = snapshot with { UsageHistory = previous.UsageHistory };
                }

                Snapshots[providerId] = snapshot;
            }
            _cache.Store(snapshot);
            AppLog.Info(LogTag.Refresh, $"{providerId} ok");
            return RefreshOutcome.Refreshed;
        }
        finally
        {
            lock (RefreshingProviderIds) { RefreshingProviderIds.Remove(providerId); }
        }
    }

    private static string? ErrorMessage(ProviderSnapshot snapshot)
    {
        var errorLine = snapshot.Lines.FirstOrDefault(l => l.IsError) as MetricLine.Badge;
        return errorLine?.BadgeText;
    }

    public void ClearFailureBackoff(string providerId)
    {
        lock (_mutationLock) { _failureRetryAfter.Remove(providerId); }
    }

    public void ProviderEnablementDidChange()
    {
        // No peer-history union to rebuild yet (iCloud sync not ported); snapshots already reflect
        // enablement through the caller's own filtering. Kept as a hook for parity with the Swift API.
    }

    public string? Plan(string providerId) => Snapshots.GetValueOrDefault(providerId)?.Plan;

    public static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(RefreshSetting.DefaultMinutes * 2);

    public StalenessHint? StalenessHint(string providerId)
    {
        if (Snapshots.GetValueOrDefault(providerId) is not { } snapshot) return null;
        var age = _now() - snapshot.RefreshedAt;
        if (age < StalenessThreshold) return null;
        var duration = Formatters.CompactDuration(age.TotalSeconds);
        if (duration is null) return null;
        return new StalenessHint("Outdated", $"Last updated {duration} ago");
    }

    /// <summary>The single WidgetData resolution entry point every UI surface reads through.</summary>
    public WidgetData Data(WidgetDescriptor descriptor)
    {
        WidgetData result;
        if (Snapshots.TryGetValue(descriptor.ProviderId, out var snapshot)
            && snapshot.Line(descriptor.MetricLabel) is { } line
            && Resolve(line, descriptor) is { } data)
        {
            result = data;
        }
        else
        {
            result = CloneSample(descriptor.Sample);
            result.HasData = false;
        }

        result.DisplayMode = descriptor.ValuePresentation == WidgetValuePresentation.RemainingPercent
            ? WidgetDisplayMode.Remaining
            : MeterStyle;
        result.ResetDisplayMode = ResetDisplayMode;
        result.AlwaysShowPacing = AlwaysShowPacing;
        return result;
    }

    /// <summary>
    /// Resolves provider-level status badges (such as OpenCode's "Go Quota" and "Local Usage"
    /// warning/status lines) that do not map to an individual placed WidgetDescriptor, so the
    /// provider card can surface reconnect, offline, schema, and partial-data statuses alongside
    /// any valid quota or local-spend metrics.
    ///
    /// A fatal refresh failure is rendered here even when no snapshot has ever been stored, and when
    /// a last-good snapshot is retained the failure text says that the card is showing that snapshot,
    /// not current data. The retained snapshot's own status qualifiers (for example OpenCode's
    /// "Local Usage: Partial Cost") are kept after the failure badge, because those caveats still
    /// apply to the last successful values. Provider errors stay presentation-only: the retained
    /// values/cache/history are never replaced by the error snapshot.
    /// </summary>
    public IReadOnlyList<WidgetData> StatusBadges(string providerId)
    {
        var hasSnapshot = Snapshots.TryGetValue(providerId, out var snapshot);
        var failure = ProviderErrors.GetValueOrDefault(providerId);
        var hasFailure = !string.IsNullOrWhiteSpace(failure);
        if (!hasSnapshot && !hasFailure) return Array.Empty<WidgetData>();

        var iconKey = _registry.Provider(providerId)?.IconKey ?? providerId;
        var badges = hasSnapshot
            ? SnapshotStatusBadges(providerId, snapshot!, iconKey)
            : new List<WidgetData>();

        if (hasFailure)
        {
            var subtitle = hasSnapshot
                ? $"{failure} Showing last successful data from {LastSuccessfulLabel(snapshot!.RefreshedAt)}."
                : failure;
            badges.Insert(0, BadgeRow("Status", "Refresh Failed", subtitle, "#EF4444", iconKey));
        }

        return badges;
    }

    /// <summary>
    /// Provider-level status badges carried by a snapshot that do not map to a placed
    /// WidgetDescriptor. Shared by the normal and the retained-last-good path so a failure keeps the
    /// same deduplicated qualifiers with the same texts and severities.
    /// </summary>
    private List<WidgetData> SnapshotStatusBadges(string providerId, ProviderSnapshot snapshot, string iconKey)
    {
        var descriptorLabels = _registry.DescriptorsFor(providerId)
            .Select(d => d.MetricLabel)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<(string Label, string BadgeText, string Subtitle)>();
        var badges = new List<WidgetData>();

        foreach (var badge in snapshot.Lines.OfType<MetricLine.Badge>())
        {
            if (badge.IsError || badge.Label == "Status" || descriptorLabels.Contains(badge.Label)) continue;
            if (!seen.Add((badge.Label, badge.BadgeText, badge.Subtitle ?? string.Empty))) continue;

            var data = new WidgetData(badge.Label, iconKey, MetricKind.Count, 0, null)
            {
                DisplayMode = MeterStyle,
                ResetDisplayMode = ResetDisplayMode,
                AlwaysShowPacing = AlwaysShowPacing
            };
            badges.Add(ApplyBadge(badge, data));
        }

        return badges;
    }

    private WidgetData BadgeRow(string label, string text, string? subtitle, string? colorHex, string iconKey)
    {
        var data = new WidgetData(label, iconKey, MetricKind.Count, 0, null)
        {
            DisplayMode = MeterStyle,
            ResetDisplayMode = ResetDisplayMode,
            AlwaysShowPacing = AlwaysShowPacing
        };
        return ApplyBadge(new MetricLine.Badge(label, text, colorHex, subtitle), data);
    }

    private static string LastSuccessfulLabel(DateTimeOffset refreshedAt) =>
        refreshedAt.ToLocalTime().ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    private static WidgetData CloneSample(WidgetData sample)
    {
        return new WidgetData(sample.Title, sample.IconKey, sample.Kind, sample.Used, sample.Limit)
        {
            CountSuffix = sample.CountSuffix,
            ValuePrefix = sample.ValuePrefix,
            LimitNoun = sample.LimitNoun,
            UnboundedValueWord = sample.UnboundedValueWord,
            InfoNote = sample.InfoNote,
            ValueTooltipNote = sample.ValueTooltipNote,
            Selection = sample.Selection,
            IsUsagePeriod = sample.IsUsagePeriod,
            TraySuffix = sample.TraySuffix,
            IsSessionWindow = sample.IsSessionWindow,
            IsChart = sample.IsChart,
            ShowsResetExpiries = sample.ShowsResetExpiries,
            BadgeSeverity = sample.BadgeSeverity,
            HasData = sample.HasData
        };
    }

    private WidgetData? Resolve(MetricLine line, WidgetDescriptor descriptor)
    {
        switch (line)
        {
            case MetricLine.Progress p:
            {
                // An opted-in widget shows the same bounded quota as percent left. Scale used and
                // limit together (rather than treating remaining as used) so fraction, pacing, and
                // severity stay identical to the provider's native-units view.
                var showsRemainingPercent = descriptor.ValuePresentation == WidgetValuePresentation.RemainingPercent;
                if (showsRemainingPercent && p.Limit <= 0) return null;

                var kind = showsRemainingPercent ? MetricKind.Percent : p.Format.MetricKind;
                var used = showsRemainingPercent
                    ? p.Used / p.Limit * 100
                    : p.Format is ProgressFormat.Percent ? Support.ProviderParse.ClampPercent(p.Used) : p.Used;
                var limit = showsRemainingPercent ? 100 : p.Limit;

                var result = new WidgetData(descriptor.Sample.Title, descriptor.Sample.IconKey, kind, used, limit)
                {
                    CountSuffix = kind == MetricKind.Percent ? null : p.Format.CountSuffix,
                    ValuePrefix = descriptor.Sample.ValuePrefix,
                    ResetsAt = p.ResetsAt,
                    PeriodDurationMs = p.PeriodDurationMs,
                    LimitNoun = descriptor.Sample.LimitNoun,
                    InfoNote = descriptor.Sample.InfoNote,
                    IsSessionWindow = descriptor.Sample.IsSessionWindow
                };
                return result;
            }
            case MetricLine.Text _:
                return null;
            case MetricLine.Values v:
            {
                var data = CloneSample(descriptor.Sample);
                data.Values = v.ValuesList;
                data.Limit = null;
                data.ExpiriesAt = v.Expiries;
                data.UnknownModels = v.Unknown;
                data.ModelBreakdown = v.ModelBreakdown;
                data.HasData = data.SelectedValues.Count > 0;
                data.InfoNote = data.SelectedValues.Any(val => val.Estimated) ? WidgetData.LocalEstimateNote : descriptor.Sample.InfoNote;
                return data;
            }
            case MetricLine.Badge b:
            {
                var data = CloneSample(descriptor.Sample);
                return ApplyBadge(b, data);
            }
            case MetricLine.Chart c:
            {
                var data = CloneSample(descriptor.Sample);
                data.IsChart = true;
                data.ChartPoints = c.Points;
                data.ChartNote = c.Note;
                data.HasData = c.Points.Count > 0;
                return data;
            }
            default:
                return null;
        }
    }

    private static WidgetData ApplyBadge(MetricLine.Badge badge, WidgetData data)
    {
        data.Limit = null;
        data.ValueTextOverride = badge.BadgeText;
        data.SubtitleOverride = badge.Subtitle;
        data.BadgeSeverity = BadgeSeverityFor(badge.ColorHex);
        data.HasData = true;
        return data;
    }

    private static WidgetData.MeterSeverity? BadgeSeverityFor(string? colorHex) =>
        colorHex?.Trim().ToUpperInvariant() switch
        {
            "#EF4444" => WidgetData.MeterSeverity.Critical,
            "#F59E0B" => WidgetData.MeterSeverity.Warning,
            "#22C55E" => WidgetData.MeterSeverity.Normal,
            _ => null
        };
}
