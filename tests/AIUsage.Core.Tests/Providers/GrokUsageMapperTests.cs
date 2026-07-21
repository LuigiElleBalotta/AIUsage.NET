using AIUsage.Core.Models;
using AIUsage.Core.Providers.Grok;
using AIUsage.Core.Tests.TestHelpers;

namespace AIUsage.Core.Tests.Providers;

public class GrokCreditsConfigDecoderTests
{
    private static byte[] Body(string json) => System.Text.Encoding.UTF8.GetBytes(json);

    [Fact]
    public void Decode_WeeklyPeriod_ParsesAllFields()
    {
        var json = """
        {
          "config": {
            "currentPeriod": { "type": "USAGE_PERIOD_TYPE_WEEKLY", "start": "2026-07-14T00:00:00Z", "end": "2026-07-21T00:00:00Z" },
            "creditUsagePercent": 42.5,
            "onDemandCap": { "val": 10 }
          }
        }
        """;

        var config = GrokCreditsConfigDecoder.Decode(Body(json));

        Assert.Equal(GrokCreditsConfigDecoder.WeeklyPeriodType, config.PeriodType);
        Assert.Equal(42.5, config.UsedPercent);
        Assert.Equal(10, config.OnDemandCap);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero), config.PeriodStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), config.PeriodEnd);
        Assert.Equal((long)TimeSpan.FromDays(7).TotalMilliseconds, config.PeriodDurationMs);
    }

    [Fact]
    public void Decode_MissingConfig_Throws()
    {
        Assert.Throws<GrokUsageError>(() => GrokCreditsConfigDecoder.Decode(Body("{}")));
    }

    [Fact]
    public void Decode_MissingCurrentPeriod_Throws()
    {
        var json = """{ "config": {} }""";
        Assert.Throws<GrokUsageError>(() => GrokCreditsConfigDecoder.Decode(Body(json)));
    }

    [Fact]
    public void Decode_MissingPeriodType_Throws()
    {
        var json = """
        { "config": { "currentPeriod": { "start": "2026-07-14T00:00:00Z", "end": "2026-07-21T00:00:00Z" } } }
        """;
        Assert.Throws<GrokUsageError>(() => GrokCreditsConfigDecoder.Decode(Body(json)));
    }

    [Fact]
    public void Decode_MissingStartOrEnd_Throws()
    {
        var json = """
        { "config": { "currentPeriod": { "type": "USAGE_PERIOD_TYPE_WEEKLY", "end": "2026-07-21T00:00:00Z" } } }
        """;
        Assert.Throws<GrokUsageError>(() => GrokCreditsConfigDecoder.Decode(Body(json)));
    }

    [Fact]
    public void Decode_EndBeforeStart_Throws()
    {
        var json = """
        { "config": { "currentPeriod": { "type": "USAGE_PERIOD_TYPE_WEEKLY", "start": "2026-07-21T00:00:00Z", "end": "2026-07-14T00:00:00Z" } } }
        """;
        Assert.Throws<GrokUsageError>(() => GrokCreditsConfigDecoder.Decode(Body(json)));
    }

    [Fact]
    public void Decode_InvalidCreditUsagePercent_Throws()
    {
        var json = """
        {
          "config": {
            "currentPeriod": { "type": "USAGE_PERIOD_TYPE_WEEKLY", "start": "2026-07-14T00:00:00Z", "end": "2026-07-21T00:00:00Z" },
            "creditUsagePercent": "not-a-number"
          }
        }
        """;
        Assert.Throws<GrokUsageError>(() => GrokCreditsConfigDecoder.Decode(Body(json)));
    }

    [Fact]
    public void Decode_InvalidOnDemandCap_Throws()
    {
        var json = """
        {
          "config": {
            "currentPeriod": { "type": "USAGE_PERIOD_TYPE_WEEKLY", "start": "2026-07-14T00:00:00Z", "end": "2026-07-21T00:00:00Z" },
            "onDemandCap": "not-an-object"
          }
        }
        """;
        Assert.Throws<GrokUsageError>(() => GrokCreditsConfigDecoder.Decode(Body(json)));
    }

    [Fact]
    public void Decode_MissingOptionalFields_DefaultsToZero()
    {
        var json = """
        { "config": { "currentPeriod": { "type": "USAGE_PERIOD_TYPE_DAILY", "start": "2026-07-14T00:00:00Z", "end": "2026-07-15T00:00:00Z" } } }
        """;
        var config = GrokCreditsConfigDecoder.Decode(Body(json));
        Assert.Equal(0, config.UsedPercent);
        Assert.Equal(0, config.OnDemandCap);
    }
}

public class GrokUsageMapperTests
{
    [Fact]
    public void MapCreditsConfig_WeeklyPeriod_IncludesProgressLine()
    {
        var json = """
        {
          "config": {
            "currentPeriod": { "type": "USAGE_PERIOD_TYPE_WEEKLY", "start": "2026-07-14T00:00:00Z", "end": "2026-07-21T00:00:00Z" },
            "creditUsagePercent": 55,
            "onDemandCap": { "val": 20 }
          }
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = GrokUsageMapper.MapCreditsConfig(response);

        var weekly = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Weekly limit"));
        Assert.Equal(55, weekly.Used);
        var badge = Assert.IsType<MetricLine.Badge>(mapped.Lines.Single(l => l.Label == "Pay as you go"));
        Assert.Equal("20 cap", badge.BadgeText);
        Assert.Equal("#22c55e", badge.ColorHex);
    }

    [Fact]
    public void MapCreditsConfig_NonWeeklyPeriod_OmitsProgressLine()
    {
        var json = """
        { "config": { "currentPeriod": { "type": "USAGE_PERIOD_TYPE_DAILY", "start": "2026-07-20T00:00:00Z", "end": "2026-07-21T00:00:00Z" } } }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = GrokUsageMapper.MapCreditsConfig(response);

        Assert.DoesNotContain(mapped.Lines, l => l.Label == "Weekly limit");
    }

    [Fact]
    public void MapCreditsConfig_NoOnDemandCap_ShowsDisabledBadge()
    {
        var json = """
        { "config": { "currentPeriod": { "type": "USAGE_PERIOD_TYPE_DAILY", "start": "2026-07-20T00:00:00Z", "end": "2026-07-21T00:00:00Z" } } }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = GrokUsageMapper.MapCreditsConfig(response);

        var badge = Assert.IsType<MetricLine.Badge>(mapped.Lines.Single(l => l.Label == "Pay as you go"));
        Assert.Equal("Disabled", badge.BadgeText);
        Assert.Equal("#a3a3a3", badge.ColorHex);
    }

    [Fact]
    public void MapCreditsConfig_Unauthorized_ThrowsAuthError()
    {
        var response = HttpResponseFixture.Json("{}", statusCode: 401);
        Assert.Throws<GrokAuthError>(() => GrokUsageMapper.MapCreditsConfig(response));
    }

    [Fact]
    public void MapCreditsConfig_ServerError_ThrowsUsageError()
    {
        var response = HttpResponseFixture.Json("{}", statusCode: 500);
        Assert.Throws<GrokUsageError>(() => GrokUsageMapper.MapCreditsConfig(response));
    }

    [Fact]
    public void PlanName_ReadsSubscriptionTierDisplay()
    {
        var response = HttpResponseFixture.Json("""{ "subscription_tier_display": "SuperGrok Heavy" }""");
        Assert.Equal("SuperGrok Heavy", GrokUsageMapper.PlanName(response));
    }

    [Fact]
    public void PlanName_NonSuccessStatus_ReturnsNull()
    {
        var response = HttpResponseFixture.Json("""{ "subscription_tier_display": "SuperGrok" }""", statusCode: 404);
        Assert.Null(GrokUsageMapper.PlanName(response));
    }

    [Fact]
    public void PlanName_MissingField_ReturnsNull()
    {
        var response = HttpResponseFixture.Json("{}");
        Assert.Null(GrokUsageMapper.PlanName(response));
    }
}
