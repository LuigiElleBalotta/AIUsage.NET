namespace AIUsage.Core.Models;

public sealed record LimitResourceDescriptor(
    string Key,
    LimitResourceDescriptor.ResourceKind Kind,
    string Unit,
    LimitResourceDescriptor.ResourceSource Source,
    bool Estimated = false)
{
    public enum ResourceKind
    {
        Consumption,
        Balance
    }

    public abstract record ResourceSource
    {
        public sealed record Progress : ResourceSource;
        public sealed record Value(MetricKind Kind, string? Label = null) : ResourceSource;
        public sealed record ProgressOrValue(MetricKind Kind, string? Label = null) : ResourceSource;

        public static readonly ResourceSource ProgressValue = new Progress();
    }
}

public enum UsageHistoryScope
{
    MachineLocal,
    AccountWide
}

public sealed record UsageHistoryDescriptor(UsageHistoryScope Scope, bool EstimatedCost, string SourceNote);

/// <summary>
/// A provider metric's identity and presentation template. Direct port of Swift's WidgetDescriptor.
/// </summary>
public sealed record WidgetDescriptor(
    string Id,
    string ProviderId,
    string MetricLabel,
    WidgetData Sample,
    bool Pinnable = true,
    bool IsSpendTile = false,
    IReadOnlyList<LimitResourceDescriptor>? LimitResources = null,
    UsageHistoryDescriptor? HistoryResource = null)
{
    public string Title => Sample.Title;

    public WidgetDescriptor ExportingLimit(
        string key,
        LimitResourceDescriptor.ResourceKind kind = LimitResourceDescriptor.ResourceKind.Consumption,
        string unit = "",
        LimitResourceDescriptor.ResourceSource? source = null,
        bool estimated = false)
    {
        var resources = new List<LimitResourceDescriptor>(LimitResources ?? Array.Empty<LimitResourceDescriptor>())
        {
            new(key, kind, unit, source ?? LimitResourceDescriptor.ResourceSource.ProgressValue, estimated)
        };
        return this with { LimitResources = resources };
    }

    public WidgetDescriptor ExportingHistory(UsageHistoryScope scope, bool estimatedCost, string sourceNote)
    {
        return this with { HistoryResource = new UsageHistoryDescriptor(scope, estimatedCost, sourceNote) };
    }
}
