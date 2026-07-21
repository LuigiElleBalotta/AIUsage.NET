using AIUsage.Core.Models;
using AIUsage.Core.Providers.Claude;
using AIUsage.Core.Tests.TestHelpers;

namespace AIUsage.Core.Tests.Providers;

public class ClaudeUsageMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);

    private static ClaudeOAuth Credentials(string? subscriptionType = "pro", string? rateLimitTier = null) => new()
    {
        AccessToken = "token",
        SubscriptionType = subscriptionType,
        RateLimitTier = rateLimitTier
    };

    [Fact]
    public void MapUsageResponse_SessionAndWeeklyWindows_ProduceProgressLines()
    {
        var json = """
        {
          "five_hour": { "utilization": 95, "resets_at": "2026-07-21T12:39:59.731346+00:00" },
          "seven_day": { "utilization": 12, "resets_at": "2026-07-24T04:59:59.731376+00:00" }
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = ClaudeUsageMapper.MapUsageResponse(response, Credentials(), Now);

        var session = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Session"));
        Assert.Equal(95, session.Used);
        var weekly = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Weekly"));
        Assert.Equal(12, weekly.Used);
    }

    [Fact]
    public void MapUsageResponse_ScopedWeeklyLimit_MapsFableLine()
    {
        var json = """
        {
          "limits": [
            {
              "kind": "weekly_scoped",
              "percent": 40,
              "resets_at": "2026-07-24T00:00:00Z",
              "scope": { "model": { "display_name": "Fable" } }
            }
          ]
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = ClaudeUsageMapper.MapUsageResponse(response, Credentials(), Now);

        var fable = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Fable"));
        Assert.Equal(40, fable.Used);
    }

    [Fact]
    public void MapUsageResponse_ExtraUsageWithMonthlyLimit_ProducesBoundedProgress()
    {
        var json = """
        {
          "five_hour": { "utilization": 10 },
          "extra_usage": { "is_enabled": true, "used_credits": 2500, "monthly_limit": 10000 }
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = ClaudeUsageMapper.MapUsageResponse(response, Credentials(), Now);

        var extra = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Extra usage spent"));
        Assert.Equal(25.0, extra.Used);   // 2500 cents -> $25
        Assert.Equal(100.0, extra.Limit); // 10000 cents -> $100
    }

    [Fact]
    public void MapUsageResponse_ExtraUsageWithoutLimit_ProducesUnboundedValue()
    {
        var json = """
        {
          "extra_usage": { "is_enabled": true, "used_credits": 150 }
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = ClaudeUsageMapper.MapUsageResponse(response, Credentials(), Now);

        var extra = Assert.IsType<MetricLine.Values>(mapped.Lines.Single(l => l.Label == "Extra usage spent"));
        Assert.Equal(1.5, extra.ValuesList[0].Number);
    }

    [Fact]
    public void MapUsageResponse_ExtraUsageDisabled_IsOmitted()
    {
        var json = """{ "extra_usage": { "is_enabled": false, "used_credits": 100 } }""";
        var response = HttpResponseFixture.Json(json);

        var mapped = ClaudeUsageMapper.MapUsageResponse(response, Credentials(), Now);

        Assert.DoesNotContain(mapped.Lines, l => l.Label == "Extra usage spent");
    }

    [Fact]
    public void MapUsageResponse_UnauthorizedStatus_ThrowsAuthError()
    {
        var response = HttpResponseFixture.Json("{}", statusCode: 401);
        Assert.Throws<ClaudeAuthError>(() => ClaudeUsageMapper.MapUsageResponse(response, Credentials(), Now));
    }

    [Fact]
    public void MapUsageResponse_ServerError_ThrowsUsageError()
    {
        var response = HttpResponseFixture.Json("{}", statusCode: 500);
        Assert.Throws<ClaudeUsageError>(() => ClaudeUsageMapper.MapUsageResponse(response, Credentials(), Now));
    }

    [Fact]
    public void MapUsageResponse_EmptyBody_ThrowsInvalidResponse()
    {
        var response = HttpResponseFixture.Empty(200);
        Assert.Throws<ClaudeUsageError>(() => ClaudeUsageMapper.MapUsageResponse(response, Credentials(), Now));
    }

    [Theory]
    [InlineData("pro", null, "Pro")]
    [InlineData("team", "20x", "Team 20x")]
    [InlineData(null, "20x", null)]
    public void FormatPlan_CombinesSubscriptionAndRateLimitTier(string? subscription, string? tier, string? expected)
    {
        Assert.Equal(expected, ClaudeUsageMapper.FormatPlan(subscription, tier));
    }

    [Fact]
    public void RateLimitedUsage_IncludesRetryTimeInWarningAndNote()
    {
        var mapped = ClaudeUsageMapper.RateLimitedUsage(Credentials(), retryAfterSeconds: 125);
        Assert.Contains("3m", mapped.Warning); // ceil(125/60) = 3
        Assert.Contains(mapped.Lines, l => l is MetricLine.Badge);
        Assert.Contains(mapped.Lines, l => l is MetricLine.Text);
    }

    [Fact]
    public void ParseRetryAfterSeconds_NumericHeader_ParsesDirectly()
    {
        var response = HttpResponseFixture.Json("{}", headers: new Dictionary<string, string> { ["retry-after"] = "42" });
        var seconds = ClaudeUsageMapper.ParseRetryAfterSeconds(response, Now);
        Assert.Equal(42, seconds);
    }

    [Fact]
    public void ParseRetryAfterSeconds_MissingHeader_ReturnsNull()
    {
        var response = HttpResponseFixture.Json("{}");
        Assert.Null(ClaudeUsageMapper.ParseRetryAfterSeconds(response, Now));
    }
}
