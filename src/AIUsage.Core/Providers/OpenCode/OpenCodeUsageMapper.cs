using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

/// <summary>
/// Turns the authoritative Console quota into the three existing cap meters. Every number — used,
/// limit, reset, and window — comes from the server; nothing is derived from local spend or a
/// hardcoded plan. The metric labels/IDs stay the same so existing layout and pinning are preserved.
/// </summary>
public static class OpenCodeUsageMapper
{
    public static string PlanName(OpenCodeGoStatus status)
    {
        var product = status.Product.Trim();
        return product.ToLowerInvariant() switch
        {
            "go" => "Go",
            "go-plus" or "goplus" => "Go Plus",
            _ => product.TitleCased(c => c is '-' or '_' or ' ').NilIfEmpty() ?? "Go"
        };
    }

    public static List<MetricLine> MeterLines(OpenCodeGoStatus status)
    {
        var lines = new List<MetricLine>();
        Add(lines, "Session", status.FiveHour);
        Add(lines, "Weekly", status.Week);
        Add(lines, "Monthly", status.Month);
        return lines;
    }

    private static void Add(List<MetricLine> lines, string label, OpenCodeGoMeter? meter)
    {
        if (meter is null) return;
        lines.Add(new MetricLine.Progress(
            label,
            meter.UsedUsd,
            meter.LimitUsd,
            ProgressFormat.DollarsValue,
            meter.ResetsAt,
            meter.PeriodDurationMs));
    }

    public const string QuotaBadgeLabel = "Go Quota";
    public const string LocalUsageBadgeLabel = "Local Usage";

    public static MetricLine QuotaBadge(string value, string colorHex, string subtitle) =>
        new MetricLine.Badge(QuotaBadgeLabel, value, colorHex, subtitle);

    public static MetricLine LocalUsageBadge(string value, string colorHex, string subtitle) =>
        new MetricLine.Badge(LocalUsageBadgeLabel, value, colorHex, subtitle);

    public const string QuotaSourceNote = "Local log spend; Go quota is server-normalized by OpenCode Console";
}
