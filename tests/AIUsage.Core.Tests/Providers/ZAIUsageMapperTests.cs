using AIUsage.Core.Models;
using AIUsage.Core.Providers.ZAI;

namespace AIUsage.Core.Tests.Providers;

public class ZAIUsageMapperTests
{
    private static byte[] Body(string json) => System.Text.Encoding.UTF8.GetBytes(json);

    [Fact]
    public void MapQuota_TokenLimitUnit3_ClassifiesAsSession()
    {
        // unit 3 = hourly; number 5 -> 5h window (session)
        var json = """
        {
          "limits": [
            { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 30, "nextResetTime": 1700000000000 }
          ]
        }
        """;

        var lines = ZAIUsageMapper.MapQuota(Body(json));

        var session = Assert.IsType<MetricLine.Progress>(lines.Single(l => l.Label == "Session"));
        Assert.Equal(30, session.Used);
    }

    [Fact]
    public void MapQuota_TokenLimitUnit6_ClassifiesAsWeekly()
    {
        // unit 6 = weekly; number 1 -> 7 days (weekly)
        var json = """
        {
          "limits": [
            { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 55 }
          ]
        }
        """;

        var lines = ZAIUsageMapper.MapQuota(Body(json));

        var weekly = Assert.IsType<MetricLine.Progress>(lines.Single(l => l.Label == "Weekly"));
        Assert.Equal(55, weekly.Used);
    }

    [Fact]
    public void MapQuota_TimeLimit_ProducesWebSearchLine()
    {
        var json = """
        {
          "limits": [
            { "type": "TIME_LIMIT", "currentValue": 3, "usage": 20 }
          ]
        }
        """;

        var lines = ZAIUsageMapper.MapQuota(Body(json));

        var web = Assert.IsType<MetricLine.Progress>(lines.Single(l => l.Label == "Web Searches"));
        Assert.Equal(3, web.Used);
        Assert.Equal(20, web.Limit);
    }

    [Fact]
    public void MapQuota_NoLimits_ReturnsNoUsageData()
    {
        var json = """{ "limits": [] }""";
        var lines = ZAIUsageMapper.MapQuota(Body(json));
        Assert.Single(lines);
        Assert.Same(MetricLine.NoUsageData, lines[0]);
    }

    [Fact]
    public void MapQuota_UnrecognizedUnit_YieldsNoUsageData()
    {
        // A TOKENS_LIMIT entry with an unrecognized unit produces no classifiable window
        // (ClassifyTokenWindow returns null) and is skipped without being counted as "recognized",
        // so with no other usable lines, MapQuota reports NoUsageData rather than throwing.
        var json = """
        {
          "limits": [
            { "type": "TOKENS_LIMIT", "unit": 99, "number": 1, "percentage": 10 }
          ]
        }
        """;

        var lines = ZAIUsageMapper.MapQuota(Body(json));
        Assert.Single(lines);
        Assert.Same(MetricLine.NoUsageData, lines[0]);
    }

    [Fact]
    public void MapQuota_InvalidPercentage_ThrowsZAIUsageError()
    {
        var json = """
        {
          "limits": [
            { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": "oops" }
          ]
        }
        """;

        Assert.Throws<ZAIUsageError>(() => ZAIUsageMapper.MapQuota(Body(json)));
    }

    [Fact]
    public void MapQuota_InvalidUnit_ThrowsZAIUsageError()
    {
        var json = """
        {
          "limits": [
            { "type": "TOKENS_LIMIT", "unit": "not-a-number", "number": 5, "percentage": 10 }
          ]
        }
        """;

        Assert.Throws<ZAIUsageError>(() => ZAIUsageMapper.MapQuota(Body(json)));
    }

    [Fact]
    public void MapQuota_InvalidNumber_ThrowsZAIUsageError()
    {
        var json = """
        {
          "limits": [
            { "type": "TOKENS_LIMIT", "unit": 3, "number": 0, "percentage": 10 }
          ]
        }
        """;

        Assert.Throws<ZAIUsageError>(() => ZAIUsageMapper.MapQuota(Body(json)));
    }

    [Fact]
    public void MapQuota_MissingLimitsArray_ThrowsInvalidResponse()
    {
        Assert.Throws<ZAIUsageError>(() => ZAIUsageMapper.MapQuota(Body("{}")));
    }

    [Fact]
    public void MapQuota_WrappedInDataObject_IsUnwrapped()
    {
        var json = """
        { "data": { "limits": [ { "type": "TIME_LIMIT", "currentValue": 1, "usage": 10 } ] } }
        """;

        var lines = ZAIUsageMapper.MapQuota(Body(json));

        Assert.Contains(lines, l => l.Label == "Web Searches");
    }

    [Fact]
    public void IsNoCodingPlan_MatchesFailureMessage()
    {
        var json = """{ "success": false, "msg": "No active coding plan subscription found" }""";
        Assert.True(ZAIUsageMapper.IsNoCodingPlan(Body(json)));
    }

    [Fact]
    public void IsNoCodingPlan_SuccessTrue_ReturnsFalse()
    {
        var json = """{ "success": true }""";
        Assert.False(ZAIUsageMapper.IsNoCodingPlan(Body(json)));
    }

    [Fact]
    public void PlanName_ReadsFirstProductNameFromDataArray()
    {
        var json = """{ "data": [ { "productName": "GLM Coding Pro" } ] }""";
        Assert.Equal("GLM Coding Pro", ZAIUsageMapper.PlanName(Body(json)));
    }

    [Fact]
    public void PlanName_MissingDataArray_ReturnsNull()
    {
        Assert.Null(ZAIUsageMapper.PlanName(Body("{}")));
    }
}
