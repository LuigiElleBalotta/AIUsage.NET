using AIUsage.Core.Support;

namespace AIUsage.Core.Models;

/// <summary>
/// Everything a tile needs to render one metric. Direct, pragmatic port of the Swift WidgetData —
/// keeps every computed property that drives the tray label, the popover headline/subtitle, and the
/// meter severity/color, while dropping SwiftUI-only tooltip minutiae that has no WPF equivalent yet.
/// </summary>
public sealed class WidgetData
{
    public const string LocalEstimateNote = "Estimated locally, so it may be off";
    public const string CursorUsageHistoryNote = "From your Cursor usage history.";
    public const string NoDataHeadline = "\u2014";
    public const string NoDataSubtitle = "No data";

    public string Title { get; set; }
    public string IconKey { get; set; }
    public MetricKind Kind { get; set; }
    public double Used { get; set; }
    public double? Limit { get; set; }
    public string? CountSuffix { get; set; }
    public string? ValuePrefix { get; set; }
    public WidgetDisplayMode DisplayMode { get; set; } = WidgetDisplayMode.Used;
    public ResetDisplayMode ResetDisplayMode { get; set; } = ResetDisplayMode.Relative;
    public bool AlwaysShowPacing { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public IReadOnlyList<DateTimeOffset> ExpiriesAt { get; set; } = Array.Empty<DateTimeOffset>();
    public bool ShowsResetExpiries { get; set; }
    public IReadOnlyList<string> UnknownModels { get; set; } = Array.Empty<string>();
    public ModelUsageBreakdown? ModelBreakdown { get; set; }
    public long? PeriodDurationMs { get; set; }
    public string? ValueTextOverride { get; set; }
    public string? SubtitleOverride { get; set; }
    public string? LimitNoun { get; set; }
    public string? UnboundedValueWord { get; set; }
    public string? InfoNote { get; set; }
    public string? ValueTooltipNote { get; set; }
    public bool HasData { get; set; } = true;
    public IReadOnlyList<MetricValue> Values { get; set; } = Array.Empty<MetricValue>();
    public ValueSelection Selection { get; set; } = ValueSelection.AllValues;
    public bool IsUsagePeriod { get; set; }
    public string? TraySuffix { get; set; }
    public bool IsSessionWindow { get; set; }
    public bool IsChart { get; set; }
    public IReadOnlyList<MetricChartPoint> ChartPoints { get; set; } = Array.Empty<MetricChartPoint>();
    public string? ChartNote { get; set; }

    public WidgetData(string title, string iconKey, MetricKind kind, double used, double? limit = null)
    {
        Title = title;
        IconKey = iconKey;
        Kind = kind;
        Used = used;
        Limit = limit;
    }

    public bool IsBounded => Limit.HasValue;

    public bool HasModelBreakdown => HasData && IsUsagePeriod && (ModelBreakdown?.Models.Count ?? 0) > 0;

    public IReadOnlyList<MetricValue> SelectedValues => Selection.Apply(Values);

    public double DisplayedValue
    {
        get
        {
            if (DisplayMode != WidgetDisplayMode.Remaining || Limit is not { } limit) return Used;
            return Math.Max(0, limit - Used);
        }
    }

    private double RoundedDisplayValue => RoundedAtDisplayPrecision(DisplayedValue);

    private double RoundedAtDisplayPrecision(double value) => Kind switch
    {
        MetricKind.Percent => Math.Round(value, MidpointRounding.AwayFromZero),
        MetricKind.Count => Math.Round(value * 10, MidpointRounding.AwayFromZero) / 10,
        MetricKind.Dollars => Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100,
        _ => value
    };

    public double Fraction
    {
        get
        {
            if (Limit is not { } limit || limit <= 0) return 0;
            return Math.Min(Math.Max(RoundedDisplayValue / limit, 0), 1);
        }
    }

    public double RemainingFraction
    {
        get
        {
            if (Limit is not { } limit || limit <= 0) return 0;
            return Math.Min(Math.Max((limit - Used) / limit, 0), 1);
        }
    }

    public enum MeterSeverity
    {
        Normal,
        Warning,
        Critical
    }

    public abstract record MeterState
    {
        public sealed record NoData : MeterState;
        public sealed record Spent : MeterState;
        public sealed record RunningOut(string? Eta, double ProjectedFraction) : MeterState;
        public sealed record CloseToLimit(string Spare, double ProjectedFraction) : MeterState;
        public sealed record Healthy(double ProjectedFraction) : MeterState;
        public sealed record Level(MeterSeverity LevelSeverity) : MeterState;

        public MeterSeverity? Severity => this switch
        {
            NoData => null,
            Spent or RunningOut => MeterSeverity.Critical,
            CloseToLimit => MeterSeverity.Warning,
            Healthy => MeterSeverity.Normal,
            Level l => l.LevelSeverity,
            _ => null
        };

        public string? Tooltip => this switch
        {
            NoData or Level => null,
            Spent => "Limit reached",
            Healthy h => $"~{(int)Math.Round((1 - h.ProjectedFraction) * 100)}% left at reset",
            CloseToLimit c => $"~{(int)Math.Round(c.ProjectedFraction * 100)}% used at reset",
            RunningOut r when r.ProjectedFraction <= 1 => "~100% used at reset",
            RunningOut r => $"~{Math.Max(1, (int)Math.Round((r.ProjectedFraction - 1) * 100))}% over limit at reset",
            _ => null
        };
    }

    public string ValueText
    {
        get
        {
            if (!HasData) return NoDataHeadline;
            if (ValueTextOverride is not null) return ValueTextOverride;
            var selected = SelectedValues;
            if (selected.Count > 0)
            {
                var first = selected[0];
                return (ValuePrefix ?? "") + MetricFormatter.Number(first.Number, first.Kind, MetricFormatter.Style.Row);
            }
            return (ValuePrefix ?? "") + Format(DisplayedValue);
        }
    }

    public string MenuBarValue
    {
        get
        {
            if (!HasData) return ValueText;
            if (Limit is { } limit && limit > 0)
            {
                if (Kind == MetricKind.Percent)
                {
                    var percent = Math.Min(100, Math.Max(0, (int)Math.Round(DisplayedValue / limit * 100)));
                    return $"{percent}%";
                }
                return MetricFormatter.Number(DisplayedValue, Kind, MetricFormatter.Style.Tray);
            }
            var selected = SelectedValues;
            if (selected.Count > 0)
            {
                var first = selected[0];
                if (TraySuffix is not null && first.Kind == MetricKind.Count)
                {
                    return $"{MetricFormatter.Number(first.Number, MetricKind.Count, MetricFormatter.Style.Tray)} {TraySuffix}";
                }
                return MetricFormatter.StringFor(first, MetricFormatter.Style.Tray);
            }
            if (ValueTextOverride is not null) return ValueTextOverride;
            var number = MetricFormatter.Number(DisplayedValue, Kind, MetricFormatter.Style.Tray);
            if (Kind == MetricKind.Count && CountSuffix is not null) return $"{number} {CountSuffix}";
            return number;
        }
    }

    public string BoundedHeadline
    {
        get
        {
            if (ValueTextOverride is not null) return ValueTextOverride;
            return $"{ValueText} {DisplayMode.Label().ToLowerInvariant()}";
        }
    }

    public string? BoundedSubtitle
    {
        get
        {
            if (SubtitleOverride is not null) return SubtitleOverride;
            if (ResetLabel is { } resetLabel) return resetLabel;
            if (PeriodDurationMs is { } periodMs)
            {
                var duration = Formatters.CompactDuration(periodMs / 1000.0);
                if (duration is not null) return $"Resets in {duration}";
            }
            switch (Kind)
            {
                case MetricKind.Percent:
                    return null;
                case MetricKind.Dollars:
                    if (Limit is not { } limit) return null;
                    var digits = Math.Round(limit) == limit ? 0 : 2;
                    var amount = Formatters.Currency(limit, digits);
                    return $"{amount} {LimitNoun ?? "limit"}";
                case MetricKind.Count:
                    return CountSuffix;
                default:
                    return null;
            }
        }
    }

    public string Headline
    {
        get
        {
            if (!HasData) return NoDataHeadline;
            return IsBounded ? BoundedHeadline : ValueText;
        }
    }

    public string UnboundedDetail
    {
        get
        {
            if (!HasData) return NoDataSubtitle;
            if (ValueTextOverride is not null) return ValueTextOverride;
            var selected = SelectedValues;
            if (selected.Count > 0)
            {
                if (selected.Count == 1)
                {
                    var value = selected[0];
                    if (value.Kind == MetricKind.Dollars && UnboundedValueWord is not null)
                    {
                        return $"{MetricFormatter.Number(value.Number, MetricKind.Dollars, MetricFormatter.Style.Row)} {UnboundedValueWord}";
                    }
                    return MetricFormatter.StringFor(value, MetricFormatter.Style.Row);
                }
                return string.Join(" \u00b7 ", selected.Select(v => MetricFormatter.StringFor(v, MetricFormatter.Style.Row)));
            }
            var word = UnboundedValueWord ?? DisplayMode.Label().ToLowerInvariant();
            if (Kind == MetricKind.Count && CountSuffix is not null)
            {
                return $"{ValueText} {CountSuffix} {word}";
            }
            return $"{ValueText} {word}";
        }
    }

    public static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan ExpiryCriticalWindow = TimeSpan.FromHours(48);

    public static MeterSeverity ExpirySeverity(TimeSpan remaining)
    {
        if (remaining <= ExpiryCriticalWindow) return MeterSeverity.Critical;
        if (remaining <= ExpiryWarningWindow) return MeterSeverity.Warning;
        return MeterSeverity.Normal;
    }

    public MeterSeverity? ExpirySeverityNow(DateTimeOffset? nowOverride = null)
    {
        if (!HasData || ExpiriesAt.Count == 0) return null;
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var soonest = ExpiriesAt.Min();
        return ExpirySeverity(soonest - now);
    }

    public int ResetCreditCount => (int)Math.Floor(SelectedValues.FirstOrDefault()?.Number ?? 0);

    public bool HasUnknownModels => HasData && UnknownModels.Count > 0;

    public string? UnknownModelTooltip
    {
        get
        {
            if (!HasUnknownModels) return null;
            var header = UnknownModels.Count == 1 ? "Unknown model found" : "Unknown models found";
            return string.Join("\n", new[] { header }.Concat(UnknownModels.Select(m => $"- {m}")));
        }
    }

    public bool IsZeroUsage
    {
        get
        {
            if (!HasData) return false;
            var selected = SelectedValues;
            return selected.Count > 0 && selected.All(v => v.Number == 0);
        }
    }

    public string? ResetLabel => ResetsAt is { } resetsAt ? Formatters.ResetRelativeLabel(resetsAt) : null;

    private string Format(double value) => MetricFormatter.Number(value, Kind, MetricFormatter.Style.Full);

    // MARK: - Pace (meter state)

    private (double Limit, DateTimeOffset ResetsAt, double PeriodSeconds)? PaceContext()
    {
        if (!HasData || Limit is not { } limit || limit <= 0 || ResetsAt is not { } resetsAt || PeriodDurationMs is not { } periodMs || periodMs <= 0)
        {
            return null;
        }
        return (limit, resetsAt, periodMs / 1000.0);
    }

    public MeterState GetMeterState(DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        if (!HasData) return new MeterState.NoData();
        if (Limit is not { } limit || limit <= 0) return new MeterState.Level(MeterSeverity.Normal);
        if (RoundedAtDisplayPrecision(limit - Used) <= 0) return new MeterState.Spent();

        if (IsFreshSessionWindow(now)) return AbsoluteLevelState(Used, limit);

        var ctx = PaceContext();
        if (ctx is { } c)
        {
            var result = Pace.Evaluate(Used, c.Limit, c.ResetsAt, c.PeriodSeconds, now);
            if (result is not null)
            {
                switch (result.Status)
                {
                    case Pace.Status.Ahead:
                        return new MeterState.Healthy(result.ProjectedUsage / c.Limit);
                    case Pace.Status.OnTrack:
                        if (Used / c.Limit < 0.05) return AbsoluteLevelState(Used, limit);
                        var projected = result.ProjectedUsage / c.Limit;
                        var spare = (int)Math.Round((1 - projected) * 100);
                        if (spare < 1) return new MeterState.RunningOut(null, projected);
                        return new MeterState.CloseToLimit($"~{spare}% spare", projected);
                    case Pace.Status.Behind:
                        var etaSeconds = Pace.SecondsToRunOut(Used, c.Limit, c.ResetsAt, c.PeriodSeconds, now);
                        var etaText = etaSeconds is { } eta ? Formatters.CompactDuration(eta) : null;
                        var projectedBehind = result.ProjectedUsage / c.Limit;
                        return new MeterState.RunningOut(etaText is not null ? $"Limit in {etaText}" : null, projectedBehind);
                }
            }
        }
        return AbsoluteLevelState(Used, limit);
    }

    private bool IsFreshSessionWindow(DateTimeOffset now) => IsSessionWindow && Used <= 0;

    private static MeterState AbsoluteLevelState(double used, double limit)
    {
        var share = limit > 0 ? used / limit : 0;
        if (share >= 0.9) return new MeterState.Level(MeterSeverity.Critical);
        if (share >= 0.8) return new MeterState.Level(MeterSeverity.Warning);
        return new MeterState.Level(MeterSeverity.Normal);
    }
}
