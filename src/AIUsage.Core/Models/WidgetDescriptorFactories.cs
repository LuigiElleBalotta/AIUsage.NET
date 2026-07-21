namespace AIUsage.Core.Models;

/// <summary>
/// Shared descriptor factories, mirroring WidgetDescriptor+Factories.swift.
/// </summary>
public static class WidgetDescriptorFactories
{
    public static WidgetDescriptor Percent(string id, Provider provider, string title, string? metricLabel = null, bool isSessionWindow = false)
    {
        var sample = new WidgetData(title, provider.IconKey, MetricKind.Percent, 0, 100) { IsSessionWindow = isSessionWindow };
        return Make(id, provider, metricLabel ?? title, sample);
    }

    public static WidgetDescriptor BoundedDollars(string id, Provider provider, string title, double limit, string? metricLabel = null, string? limitNoun = null, string? valueWord = null)
    {
        var sample = new WidgetData(title, provider.IconKey, MetricKind.Dollars, 0, limit) { LimitNoun = limitNoun, UnboundedValueWord = valueWord };
        return Make(id, provider, metricLabel ?? title, sample);
    }

    public static WidgetDescriptor BoundedCount(string id, Provider provider, string title, double limit, string suffix, string? metricLabel = null, long? periodDurationMs = null)
    {
        var sample = new WidgetData(title, provider.IconKey, MetricKind.Count, 0, limit) { CountSuffix = suffix, PeriodDurationMs = periodDurationMs };
        return Make(id, provider, metricLabel ?? title, sample);
    }

    public static WidgetDescriptor Values(
        string id, Provider provider, string title,
        string? metricLabel = null,
        ValueSelection? selection = null,
        string? valueWord = null,
        bool isUsagePeriod = false,
        string? traySuffix = null,
        bool showsResetExpiries = false)
    {
        var sel = selection ?? ValueSelection.AllValues;
        var kind = sel is ValueSelection.OfKind ok ? ok.Kind : MetricKind.Dollars;
        var sample = new WidgetData(title, provider.IconKey, kind, 0, null)
        {
            UnboundedValueWord = valueWord,
            Selection = sel,
            IsUsagePeriod = isUsagePeriod,
            TraySuffix = traySuffix,
            ShowsResetExpiries = showsResetExpiries
        };
        return Make(id, provider, metricLabel ?? title, sample);
    }

    public static WidgetDescriptor Combined(string id, Provider provider, string title, string? metricLabel = null, bool isUsagePeriod = false)
    {
        return Values(id, provider, title, metricLabel, ValueSelection.AllValues, isUsagePeriod: isUsagePeriod);
    }

    public static List<WidgetDescriptor> SpendTiles(Provider provider, string? valueTooltipNote = null)
    {
        var descriptors = new List<WidgetDescriptor>
        {
            Combined($"{provider.Id}.today", provider, "Today", isUsagePeriod: true),
            Combined($"{provider.Id}.yesterday", provider, "Yesterday", isUsagePeriod: true),
            Combined($"{provider.Id}.last30", provider, "Last 30 Days", isUsagePeriod: true)
        };
        return descriptors.Select(d =>
        {
            var sample = d.Sample;
            sample.ValueTooltipNote = valueTooltipNote;
            return d with { Sample = sample, IsSpendTile = true };
        }).ToList();
    }

    public static WidgetDescriptor DollarBalance(string id, Provider provider, string title, string valueWord, string? metricLabel = null)
    {
        var sample = new WidgetData(title, provider.IconKey, MetricKind.Dollars, 0, null) { UnboundedValueWord = valueWord };
        return Make(id, provider, metricLabel ?? title, sample);
    }

    public static WidgetDescriptor Badge(string id, Provider provider, string title, string? metricLabel = null)
    {
        var sample = new WidgetData(title, provider.IconKey, MetricKind.Count, 0, null);
        return Make(id, provider, metricLabel ?? title, sample);
    }

    public static WidgetDescriptor UsageTrend(Provider provider)
    {
        var sample = new WidgetData("Usage Trend", provider.IconKey, MetricKind.Count, 0, null) { IsChart = true };
        return Make($"{provider.Id}.trend", provider, "Usage Trend", sample, pinnable: false);
    }

    private static WidgetDescriptor Make(string id, Provider provider, string metricLabel, WidgetData sample, bool pinnable = true)
    {
        return new WidgetDescriptor(id, provider.Id, metricLabel, sample, pinnable);
    }
}
