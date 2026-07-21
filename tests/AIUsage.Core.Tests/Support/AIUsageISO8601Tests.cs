using AIUsage.Core.Support;

namespace AIUsage.Core.Tests.Support;

public class AIUsageISO8601Tests
{
    [Fact]
    public void Parse_StandardIso8601_Parses()
    {
        var result = AIUsageISO8601.Parse("2026-03-26T13:00:00.161Z");
        Assert.NotNull(result);
        Assert.Equal(2026, result!.Value.Year);
        Assert.Equal(3, result.Value.Month);
        Assert.Equal(26, result.Value.Day);
    }

    [Fact]
    public void Parse_SpaceSeparatedDateTime_Normalizes()
    {
        var result = AIUsageISO8601.Parse("2026-03-26 13:00:00");
        Assert.NotNull(result);
        Assert.Equal(13, result!.Value.Hour);
    }

    [Fact]
    public void Parse_UtcSuffix_Normalizes()
    {
        var result = AIUsageISO8601.Parse("2026-03-26 13:00:00 UTC");
        Assert.NotNull(result);
        Assert.Equal(13, result!.Value.Hour);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.Null(AIUsageISO8601.Parse(""));
    }

    [Fact]
    public void Parse_InvalidString_ReturnsNull()
    {
        Assert.Null(AIUsageISO8601.Parse("not a date"));
    }

    [Fact]
    public void ToStringIso_RoundTrips()
    {
        var date = new DateTimeOffset(2026, 3, 26, 13, 0, 0, 161, TimeSpan.Zero);
        var formatted = AIUsageISO8601.ToStringIso(date);
        Assert.Equal("2026-03-26T13:00:00.161Z", formatted);

        var parsed = AIUsageISO8601.Parse(formatted);
        Assert.NotNull(parsed);
        Assert.Equal(date, parsed!.Value);
    }
}
