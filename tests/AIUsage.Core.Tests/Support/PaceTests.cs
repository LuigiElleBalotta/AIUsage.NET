using AIUsage.Core.Support;

namespace AIUsage.Core.Tests.Support;

public class PaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_ReturnsNull_WhenElapsedBelowMinimum()
    {
        // Window just started (5 hour window, only 1 second elapsed) -> too early to project.
        var resetsAt = Now.AddHours(5).AddSeconds(-1);
        var result = Pace.Evaluate(used: 1, limit: 100, resetsAt, periodDurationSeconds: 5 * 3600, Now);
        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_ReturnsNull_WhenAlreadyPastReset()
    {
        var resetsAt = Now.AddSeconds(-1);
        var result = Pace.Evaluate(used: 10, limit: 100, resetsAt, periodDurationSeconds: 3600, Now);
        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_Ahead_WhenNoUsageYet()
    {
        var resetsAt = Now.AddHours(4); // 1 hour elapsed of a 5-hour window
        var result = Pace.Evaluate(used: 0, limit: 100, resetsAt, periodDurationSeconds: 5 * 3600, Now);
        Assert.NotNull(result);
        Assert.Equal(Pace.Status.Ahead, result!.Status);
        Assert.Equal(0, result.ProjectedUsage);
    }

    [Fact]
    public void Evaluate_Behind_WhenUsedExceedsLimit()
    {
        var resetsAt = Now.AddHours(4);
        var result = Pace.Evaluate(used: 150, limit: 100, resetsAt, periodDurationSeconds: 5 * 3600, Now);
        Assert.NotNull(result);
        Assert.Equal(Pace.Status.Behind, result!.Status);
    }

    [Fact]
    public void Evaluate_OnTrack_WhenProjectedNearLimit()
    {
        // 1 hour elapsed of 5-hour window, used 19 of 100 -> projected = 19/1*5 = 95 (within 90-100 band)
        var resetsAt = Now.AddHours(4);
        var result = Pace.Evaluate(used: 19, limit: 100, resetsAt, periodDurationSeconds: 5 * 3600, Now);
        Assert.NotNull(result);
        Assert.Equal(Pace.Status.OnTrack, result!.Status);
        Assert.Equal(95, result.ProjectedUsage, precision: 3);
    }

    [Fact]
    public void Evaluate_Ahead_WhenProjectedWellUnderLimit()
    {
        // 1 hour elapsed, used 5 of 100 -> projected = 25, well under 90% of limit
        var resetsAt = Now.AddHours(4);
        var result = Pace.Evaluate(used: 5, limit: 100, resetsAt, periodDurationSeconds: 5 * 3600, Now);
        Assert.NotNull(result);
        Assert.Equal(Pace.Status.Ahead, result!.Status);
    }

    [Fact]
    public void Evaluate_ReturnsNull_WhenLimitOrPeriodNonPositive()
    {
        var resetsAt = Now.AddHours(4);
        Assert.Null(Pace.Evaluate(10, 0, resetsAt, 3600, Now));
        Assert.Null(Pace.Evaluate(10, 100, resetsAt, 0, Now));
    }

    [Fact]
    public void SecondsToRunOut_ReturnsPositiveEta_WhenBehind()
    {
        // 1 hour elapsed of a 5-hour window, used 95 of 100 (still under the limit) but the burn
        // rate projects far past it (475) -> Behind, with a positive ETA before the actual reset.
        var resetsAt = Now.AddHours(4);
        var eta = Pace.SecondsToRunOut(used: 95, limit: 100, resetsAt, periodDurationSeconds: 5 * 3600, Now);
        Assert.NotNull(eta);
        Assert.True(eta > 0);
    }

    [Fact]
    public void SecondsToRunOut_ReturnsNull_WhenAlreadyOverLimit()
    {
        // Usage already exceeds the limit -> nothing left to "run out" of (limit - used is negative).
        var resetsAt = Now.AddHours(4);
        var eta = Pace.SecondsToRunOut(used: 150, limit: 100, resetsAt, periodDurationSeconds: 5 * 3600, Now);
        Assert.Null(eta);
    }

    [Fact]
    public void SecondsToRunOut_ReturnsNull_WhenNotBehind()
    {
        var resetsAt = Now.AddHours(4);
        var eta = Pace.SecondsToRunOut(used: 5, limit: 100, resetsAt, periodDurationSeconds: 5 * 3600, Now);
        Assert.Null(eta);
    }

    [Fact]
    public void MinimumElapsed_IsAtLeast60SecondsOrOnePercentOfPeriod()
    {
        Assert.Equal(60, Pace.MinimumElapsed(100));
        Assert.Equal(3600, Pace.MinimumElapsed(360000));
    }
}
