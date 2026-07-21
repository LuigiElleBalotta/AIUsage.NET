namespace AIUsage.Core.Models;

/// <summary>
/// One column of a chart line: a day's value, its axis label, and a pre-formatted hover readout.
/// </summary>
public sealed record MetricChartPoint(double Value, string Label, string? ValueLabel = null)
{
    public string Readout => ValueLabel ?? (Support.MetricFormatter.Number(Value, MetricKind.Count, Support.MetricFormatter.Style.Row) + " tokens");
}

/// <summary>
/// Provider output normalized into a small app-owned vocabulary. Mirrors the Swift MetricLine enum
/// as a closed record hierarchy (a discriminated union via inheritance).
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(MetricLineJsonConverter))]
public abstract record MetricLine
{
    public abstract string Label { get; }

    public const string ErrorBadgeLabel = "Error";

    public bool IsError => this is Badge b && b.LabelText == ErrorBadgeLabel;

    public sealed record Text(string LabelText, string Value, string? ColorHex = null, string? Subtitle = null) : MetricLine
    {
        public override string Label => LabelText;
    }

    public sealed record Chart(string LabelText, IReadOnlyList<MetricChartPoint> Points, string? Note = null) : MetricLine
    {
        public override string Label => LabelText;
    }

    public sealed record Values(
        string LabelText,
        IReadOnlyList<MetricValue> ValuesList,
        string? ColorHex = null,
        IReadOnlyList<DateTimeOffset>? ExpiriesAt = null,
        IReadOnlyList<string>? UnknownModels = null,
        ModelUsageBreakdown? ModelBreakdown = null
    ) : MetricLine
    {
        public override string Label => LabelText;
        public IReadOnlyList<DateTimeOffset> Expiries => ExpiriesAt ?? Array.Empty<DateTimeOffset>();
        public IReadOnlyList<string> Unknown => UnknownModels ?? Array.Empty<string>();
    }

    public sealed record Progress(
        string LabelText,
        double Used,
        double Limit,
        ProgressFormat Format,
        DateTimeOffset? ResetsAt = null,
        long? PeriodDurationMs = null,
        string? ColorHex = null
    ) : MetricLine
    {
        public override string Label => LabelText;
    }

    public sealed record Badge(string LabelText, string BadgeText, string? ColorHex = null, string? Subtitle = null) : MetricLine
    {
        public override string Label => LabelText;
    }

    public static readonly MetricLine NoUsageData = new Badge("Status", "No usage data", "#A3A3A3");

    public static void AppendNoDataIfNeeded(List<MetricLine> lines)
    {
        if (lines.Count == 0)
        {
            lines.Add(NoUsageData);
        }
    }
}
