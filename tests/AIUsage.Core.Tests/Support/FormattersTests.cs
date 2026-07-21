using AIUsage.Core.Support;

namespace AIUsage.Core.Tests.Support;

public class FormattersTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(30, "1m")]           // <60s rounds up to 1m
    [InlineData(90, "2m")]           // ceil(90/60)=2m
    [InlineData(3600, "1h")]
    [InlineData(3660, "1h 1m")]
    [InlineData(90000, "1d 1h")]     // 25h -> 1d 1h, minutes dropped at day scale
    public void CompactDuration_FormatsExpectedScale(double seconds, string expected)
    {
        Assert.Equal(expected, Formatters.CompactDuration(seconds));
    }

    [Fact]
    public void CompactDuration_NonPositive_ReturnsNull()
    {
        Assert.Null(Formatters.CompactDuration(0));
        Assert.Null(Formatters.CompactDuration(-5));
    }

    [Fact]
    public void CompactDuration_NonFinite_ReturnsNull()
    {
        Assert.Null(Formatters.CompactDuration(double.NaN));
    }

    [Fact]
    public void WhenLabel_Relative_WithinFiveMinutes_ReturnsSoon()
    {
        var at = Now.AddMinutes(4);
        Assert.Equal(Formatters.Imminent, Formatters.WhenLabel(at, ResetDisplayMode.Relative, Now));
    }

    [Fact]
    public void WhenLabel_Relative_BeyondFiveMinutes_ReturnsDuration()
    {
        var at = Now.AddHours(2);
        var result = Formatters.WhenLabel(at, ResetDisplayMode.Relative, Now);
        Assert.Equal("2h", result);
    }

    [Fact]
    public void WhenLabel_Absolute_Today_ReturnsTodayAtTime()
    {
        var at = Now.AddHours(3); // still today
        var result = Formatters.WhenLabel(at, ResetDisplayMode.Absolute, Now);
        Assert.StartsWith("today at", result);
    }

    [Fact]
    public void WhenLabel_Absolute_Tomorrow_ReturnsTomorrowAtTime()
    {
        var at = Now.AddDays(1);
        var result = Formatters.WhenLabel(at, ResetDisplayMode.Absolute, Now);
        Assert.StartsWith("tomorrow at", result);
    }

    [Fact]
    public void WhenLabel_Absolute_FurtherOut_ReturnsMonthDayAtTime()
    {
        var at = Now.AddDays(10);
        var result = Formatters.WhenLabel(at, ResetDisplayMode.Absolute, Now);
        Assert.Contains(" at ", result);
        Assert.DoesNotContain("today", result);
        Assert.DoesNotContain("tomorrow", result);
    }

    [Fact]
    public void WhenLabel_Absolute_PastDeadline_ReturnsSoon()
    {
        var at = Now.AddMinutes(-1);
        Assert.Equal(Formatters.Imminent, Formatters.WhenLabel(at, ResetDisplayMode.Absolute, Now));
    }

    [Fact]
    public void DeadlineLabel_Relative_PrependsPrefixWithIn()
    {
        var at = Now.AddHours(2);
        var result = Formatters.DeadlineLabel("Resets", at, ResetDisplayMode.Relative, Now);
        Assert.Equal("Resets in 2h", result);
    }

    [Fact]
    public void DeadlineLabel_Imminent_OmitsIn()
    {
        var at = Now.AddMinutes(2);
        var result = Formatters.DeadlineLabel("Resets", at, ResetDisplayMode.Relative, Now);
        Assert.Equal("Resets soon", result);
    }

    [Fact]
    public void ResetRelativeLabel_UsesResetsPrefix()
    {
        var at = Now.AddHours(3);
        var result = Formatters.ResetRelativeLabel(at, Now);
        Assert.Equal("Resets in 3h", result);
    }

    [Fact]
    public void Currency_FormatsAsUsd()
    {
        Assert.Equal("$5.17", Formatters.Currency(5.17));
        Assert.Equal("$100", Formatters.Currency(100, 0));
    }

    [Fact]
    public void MonthDayLabel_FormatsAsAbbreviatedMonthAndDay()
    {
        var date = new DateTimeOffset(2026, 3, 25, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("Mar 25", Formatters.MonthDayLabel(date));
    }
}
