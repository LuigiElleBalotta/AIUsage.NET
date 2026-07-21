using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

/// <summary>Turns the Go plan windows into the three cap meters. Direct port of OpenCodeUsageMapper.</summary>
public static class OpenCodeUsageMapper
{
    public const double SessionCap = 12;
    public const double WeeklyCap = 30;
    public const double MonthlyCap = 60;

    public static List<MetricLine> MeterLines(OpenCodeGoWindows windows) => new()
    {
        new MetricLine.Progress("Session", windows.SessionSpend, SessionCap, ProgressFormat.DollarsValue, windows.SessionResetsAt, MetricPeriod.SessionMs),
        new MetricLine.Progress("Weekly", windows.WeeklySpend, WeeklyCap, ProgressFormat.DollarsValue, windows.WeeklyResetsAt, MetricPeriod.WeekMs),
        new MetricLine.Progress("Monthly", windows.MonthlySpend, MonthlyCap, ProgressFormat.DollarsValue, windows.MonthlyResetsAt, windows.MonthlyPeriodMs)
    };
}
