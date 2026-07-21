using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

/// <summary>The three OpenCode Go plan windows. Direct port of OpenCodeGoWindows/OpenCodeGoWindowMath.</summary>
public sealed record OpenCodeGoWindows(
    double SessionSpend, DateTimeOffset? SessionResetsAt,
    double WeeklySpend, DateTimeOffset? WeeklyResetsAt,
    double MonthlySpend, DateTimeOffset? MonthlyResetsAt,
    long? MonthlyPeriodMs);

/// <summary>
/// Window math ported faithfully from the legacy opencode-go plugin: a rolling 5-hour session, a
/// UTC-ISO week (Monday start), and a month anchored to the day-of-month of the earliest-ever local
/// Go usage (calendar-month fallback when there is none). Pure and UTC-based.
/// </summary>
public static class OpenCodeGoWindowMath
{
    public static readonly double FiveHoursMs = MetricPeriod.SessionMs;
    public static readonly double WeekMs = MetricPeriod.WeekMs;

    public static OpenCodeGoWindows Compute(List<(double Ms, double Cost)> costs, double? anchorMs, DateTimeOffset now)
    {
        var nowMs = Ms(now);

        var sessionStart = nowMs - FiveHoursMs;
        var sessionSpend = SumRange(costs, sessionStart, nowMs);
        var oldestInSession = costs.Where(c => c.Ms >= sessionStart && c.Ms < nowMs).Select(c => c.Ms).DefaultIfEmpty(double.NaN).Min();
        var sessionResetBase = double.IsNaN(oldestInSession) ? nowMs : oldestInSession;
        var sessionResetsAt = DateOf(sessionResetBase + FiveHoursMs);

        var weekStart = StartOfUtcWeek(nowMs);
        var weekEnd = weekStart + WeekMs;
        var weeklySpend = SumRange(costs, weekStart, weekEnd);

        var (monthStart, monthEnd) = AnchoredMonthBounds(nowMs, anchorMs);
        var monthlySpend = SumRange(costs, monthStart, monthEnd);

        return new OpenCodeGoWindows(
            sessionSpend, sessionResetsAt,
            weeklySpend, DateOf(weekEnd),
            monthlySpend, DateOf(monthEnd),
            (long)Math.Round(monthEnd - monthStart));
    }

    private static double SumRange(List<(double Ms, double Cost)> costs, double start, double end)
    {
        var total = costs.Where(c => c.Ms >= start && c.Ms < end).Sum(c => c.Cost);
        return Math.Round(total * 10000) / 10000;
    }

    // MARK: - Week

    private static double StartOfUtcWeek(double nowMs)
    {
        var startOfToday = DateOf(nowMs).UtcDateTime.Date;
        var weekday = (int)startOfToday.DayOfWeek; // 0=Sun..6=Sat
        var daysSinceMonday = (weekday + 6) % 7; // Mon->0, Sun->6
        var monday = startOfToday.AddDays(-daysSinceMonday);
        return Ms(new DateTimeOffset(monday, TimeSpan.Zero));
    }

    // MARK: - Month (anchored to earliest usage's day-of-month)

    private static (double Start, double End) AnchoredMonthBounds(double nowMs, double? anchorMs)
    {
        if (anchorMs is not { } anchor || !double.IsFinite(anchor))
        {
            var nowDate = DateOf(nowMs).UtcDateTime;
            var start = UtcDate(nowDate.Year, nowDate.Month, 1);
            var (endYear, endMonth) = ShiftMonth(nowDate.Year, nowDate.Month, 1);
            var end = UtcDate(endYear, endMonth, 1);
            return (Ms(start), Ms(end));
        }

        var anchorDate = DateOf(anchor).UtcDateTime;
        var now = DateOf(nowMs).UtcDateTime;
        var year = now.Year;
        var month = now.Month;
        var start2 = AnchoredMonthStart(year, month, anchorDate);

        if (Ms(start2) > nowMs)
        {
            (year, month) = ShiftMonth(year, month, -1);
            start2 = AnchoredMonthStart(year, month, anchorDate);
        }
        var (nextYear, nextMonth) = ShiftMonth(year, month, 1);
        var end2 = AnchoredMonthStart(nextYear, nextMonth, anchorDate);
        return (Ms(start2), Ms(end2));
    }

    private static DateTimeOffset AnchoredMonthStart(int year, int month, DateTime anchor)
    {
        var day = Math.Min(anchor.Day, DateTime.DaysInMonth(year, month));
        var dt = new DateTime(year, month, day, anchor.Hour, anchor.Minute, anchor.Second, anchor.Millisecond, DateTimeKind.Utc);
        return new DateTimeOffset(dt);
    }

    private static (int Year, int Month) ShiftMonth(int year, int month, int delta)
    {
        var total = year * 12 + (month - 1) + delta;
        var normalizedMonth = ((total % 12) + 12) % 12;
        var newYear = (int)Math.Floor(total / 12.0);
        return (newYear, normalizedMonth + 1);
    }

    private static DateTimeOffset UtcDate(int year, int month, int day) => new(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));

    private static double Ms(DateTimeOffset date) => date.ToUnixTimeMilliseconds();
    private static DateTimeOffset DateOf(double ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
}
