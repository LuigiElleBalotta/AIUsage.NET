using AIUsage.Core.Models;
using AIUsage.Core.Providers.Codex;
using AIUsage.Core.Tests.TestHelpers;

namespace AIUsage.Core.Tests.Providers;

public class CodexUsageMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MapUsageResponse_ClassifiesWindowsByDuration_NotBySlotOrder()
    {
        // Regression test for the "Object reference not set to an instance of an object" bug:
        // primary_window duration matches the weekly period, secondary matches session — the
        // mapper must classify by duration, not by primary=Session/secondary=Weekly position.
        var json = $$"""
        {
          "rate_limit": {
            "primary_window": {"used_percent": 5, "limit_window_seconds": {{7 * 24 * 60 * 60}} },
            "secondary_window": {"used_percent": 42, "limit_window_seconds": {{5 * 60 * 60}} }
          }
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = CodexUsageMapper.MapUsageResponse(response, null, Now);

        var session = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Session"));
        Assert.Equal(42, session.Used);
        var weekly = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Weekly"));
        Assert.Equal(5, weekly.Used);
    }

    [Fact]
    public void MapUsageResponse_NoRecognizedWindowDuration_FallsBackToSlotPosition()
    {
        var json = """
        {
          "rate_limit": {
            "primary_window": {"used_percent": 10},
            "secondary_window": {"used_percent": 20}
          }
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = CodexUsageMapper.MapUsageResponse(response, null, Now);

        var session = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Session"));
        Assert.Equal(10, session.Used);
        var weekly = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Weekly"));
        Assert.Equal(20, weekly.Used);
    }

    [Fact]
    public void MapUsageResponse_UsesResponseHeadersWhenRateLimitMissing()
    {
        var response = HttpResponseFixture.Json("{}", headers: new Dictionary<string, string>
        {
            ["x-codex-primary-used-percent"] = "7",
            ["x-codex-secondary-used-percent"] = "33"
        });

        var mapped = CodexUsageMapper.MapUsageResponse(response, null, Now);

        var session = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Session"));
        Assert.Equal(7, session.Used);
        var weekly = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Weekly"));
        Assert.Equal(33, weekly.Used);
    }

    [Fact]
    public void MapUsageResponse_SparkEntry_ProducesSparkLines()
    {
        var json = $$"""
        {
          "additional_rate_limits": [
            {
              "limit_name": "gpt-5-codex-spark",
              "rate_limit": {
                "primary_window": {"used_percent": 15, "limit_window_seconds": {{5 * 60 * 60}} },
                "secondary_window": {"used_percent": 60, "limit_window_seconds": {{7 * 24 * 60 * 60}} }
              }
            }
          ]
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = CodexUsageMapper.MapUsageResponse(response, null, Now);

        var spark = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Spark"));
        Assert.Equal(15, spark.Used);
        var sparkWeekly = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Spark Weekly"));
        Assert.Equal(60, sparkWeekly.Used);
    }

    [Fact]
    public void MapUsageResponse_NoSparkEntry_OmitsSparkLines()
    {
        var response = HttpResponseFixture.Json("{}");
        var mapped = CodexUsageMapper.MapUsageResponse(response, null, Now);
        Assert.DoesNotContain(mapped.Lines, l => l.Label is "Spark" or "Spark Weekly");
    }

    [Fact]
    public void MapUsageResponse_CreditsBalance_ProducesDollarsAndCountValues()
    {
        var json = """{ "credits": { "balance": 972 } }""";
        var response = HttpResponseFixture.Json(json);

        var mapped = CodexUsageMapper.MapUsageResponse(response, null, Now);

        var credits = Assert.IsType<MetricLine.Values>(mapped.Lines.Single(l => l.Label == "Credits"));
        Assert.Equal(38.88, credits.ValuesList[0].Number, precision: 3); // 972 * 0.04
        Assert.Equal(972, credits.ValuesList[1].Number);
    }

    [Fact]
    public void ReadResetCredits_PrefersDedicatedEndpointOverEmbeddedCount()
    {
        var body = System.Text.Json.JsonDocument.Parse("""{ "rate_limit_reset_credits": { "available_count": 1 } }""").RootElement;
        var dedicated = HttpResponseFixture.Json("""{ "available_count": 5 }""");

        var resets = CodexUsageMapper.ReadResetCredits(body, dedicated);

        Assert.NotNull(resets);
        Assert.Equal(5, resets!.Value.Count);
    }

    [Fact]
    public void ReadResetCredits_DedicatedEndpointFailed_FallsBackToEmbedded()
    {
        var body = System.Text.Json.JsonDocument.Parse("""{ "rate_limit_reset_credits": { "available_count": 3 } }""").RootElement;
        var dedicated = HttpResponseFixture.Json("{}", statusCode: 500);

        var resets = CodexUsageMapper.ReadResetCredits(body, dedicated);

        Assert.NotNull(resets);
        Assert.Equal(3, resets!.Value.Count);
    }

    [Fact]
    public void ReadResetCredits_NoSourceAvailable_ReturnsNull()
    {
        var body = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        Assert.Null(CodexUsageMapper.ReadResetCredits(body, null));
    }

    [Theory]
    [InlineData("prolite", "Pro 5x")]
    [InlineData("pro", "Pro 20x")]
    [InlineData("team_plus", "Team Plus")]
    public void FormatCodexPlan_MapsKnownAndUnknownPlans(string rawPlanType, string expected)
    {
        var body = System.Text.Json.JsonDocument.Parse($$"""{ "plan_type": "{{rawPlanType}}" }""").RootElement;
        Assert.Equal(expected, CodexUsageMapper.FormatCodexPlan(body));
    }

    [Fact]
    public void FormatCodexPlan_MissingPlanType_ReturnsNull()
    {
        var body = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        Assert.Null(CodexUsageMapper.FormatCodexPlan(body));
    }

    [Fact]
    public void CreditValues_FlooredToWholeCredits()
    {
        var values = CodexUsageMapper.CreditValues(972.9);
        Assert.Equal(972, values[1].Number);
        Assert.Equal(38.88, values[0].Number, precision: 3);
    }

    [Fact]
    public void MapUsageResponse_UnauthorizedStatus_ThrowsAuthError()
    {
        var response = HttpResponseFixture.Json("{}", statusCode: 401);
        Assert.Throws<CodexAuthError>(() => CodexUsageMapper.MapUsageResponse(response, null, Now));
    }
}
