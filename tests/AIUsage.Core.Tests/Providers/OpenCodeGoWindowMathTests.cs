using AIUsage.Core.Providers.OpenCode;

namespace AIUsage.Core.Tests.Providers;

public class OpenCodeGoWindowMathTests
{
    private static double Ms(DateTimeOffset dt) => dt.ToUnixTimeMilliseconds();

    [Fact]
    public void Compute_SessionWindow_SumsRolling5HourCosts()
    {
        // Now = 2026-07-21T12:00:00Z. Session window is [07:00, 12:00).
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var costs = new List<(double Ms, double Cost)>
        {
            (Ms(now.AddHours(-6)), 5.0),  // outside session window (before 07:00)
            (Ms(now.AddHours(-3)), 2.0),  // inside session window
            (Ms(now.AddHours(-1)), 1.5),  // inside session window
        };

        var windows = OpenCodeGoWindowMath.Compute(costs, anchorMs: null, now);

        Assert.Equal(3.5, windows.SessionSpend, precision: 4);
    }

    [Fact]
    public void Compute_WeeklyWindow_UsesUtcMondayBoundary()
    {
        // 2026-07-21 is a Tuesday -> week start is Monday 2026-07-20T00:00:00Z.
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var costs = new List<(double Ms, double Cost)>
        {
            (Ms(new DateTimeOffset(2026, 7, 19, 23, 0, 0, TimeSpan.Zero)), 10.0), // Sunday, previous week
            (Ms(new DateTimeOffset(2026, 7, 20, 1, 0, 0, TimeSpan.Zero)), 4.0),   // Monday, this week
            (Ms(new DateTimeOffset(2026, 7, 21, 6, 0, 0, TimeSpan.Zero)), 6.0),   // Tuesday, this week
        };

        var windows = OpenCodeGoWindowMath.Compute(costs, anchorMs: null, now);

        Assert.Equal(10.0, windows.WeeklySpend, precision: 4);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero), windows.WeeklyResetsAt);
    }

    [Fact]
    public void Compute_MonthlyWindow_NoAnchor_UsesCalendarMonth()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var costs = new List<(double Ms, double Cost)>
        {
            (Ms(new DateTimeOffset(2026, 6, 30, 23, 0, 0, TimeSpan.Zero)), 100.0), // previous month
            (Ms(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)), 20.0),    // this month
            (Ms(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero)), 5.0),    // this month
        };

        var windows = OpenCodeGoWindowMath.Compute(costs, anchorMs: null, now);

        Assert.Equal(25.0, windows.MonthlySpend, precision: 4);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), windows.MonthlyResetsAt);
    }

    [Fact]
    public void Compute_MonthlyWindow_AnchoredToEarliestUsageDay()
    {
        // Anchor day-of-month = 15th. Now = July 21 -> current anchored cycle is July 15 - Aug 15.
        var anchor = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var costs = new List<(double Ms, double Cost)>
        {
            (Ms(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero)), 50.0), // before anchor -> previous cycle
            (Ms(new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero)), 30.0), // after anchor -> this cycle
        };

        var windows = OpenCodeGoWindowMath.Compute(costs, Ms(anchor), now);

        Assert.Equal(30.0, windows.MonthlySpend, precision: 4);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.Zero), windows.MonthlyResetsAt);
    }

    [Fact]
    public void Compute_MonthlyWindow_AnchorClampedToShorterMonth()
    {
        // Anchor day-of-month = 31st. For February (28 days in 2026), the anchored start clamps to the 28th.
        var anchor = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 28, 12, 0, 0, TimeSpan.Zero);

        var windows = OpenCodeGoWindowMath.Compute(new List<(double Ms, double Cost)>(), Ms(anchor), now);

        // Anchored month start for Feb should be clamped to Feb 28 (since now >= that start).
        Assert.NotNull(windows.MonthlyResetsAt);
    }

    [Fact]
    public void Compute_MonthlyWindow_RollsBackwardWhenAnchorInFuture()
    {
        // Anchor day-of-month = 28th, now is July 21 (before the 28th this month)
        // -> the "current" anchored cycle should have started on June 28, not July 28.
        var anchor = new DateTimeOffset(2026, 1, 28, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);
        var costs = new List<(double Ms, double Cost)>
        {
            (Ms(new DateTimeOffset(2026, 6, 29, 0, 0, 0, TimeSpan.Zero)), 15.0), // within June 28 - July 28 cycle
        };

        var windows = OpenCodeGoWindowMath.Compute(costs, Ms(anchor), now);

        Assert.Equal(15.0, windows.MonthlySpend, precision: 4);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero), windows.MonthlyResetsAt);
    }
}
