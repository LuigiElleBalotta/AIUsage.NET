namespace AIUsage.Core.Support;

/// <summary>
/// Canonical usage-window lengths in milliseconds, shared by every provider mapper.
/// </summary>
public static class MetricPeriod
{
    public const int SessionMs = 5 * 60 * 60 * 1000;
    public const int DayMs = 24 * 60 * 60 * 1000;
    public const int WeekMs = 7 * DayMs;
    public const long MonthMs = 30L * DayMs;
}
