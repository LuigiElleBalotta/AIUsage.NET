using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Providers.OpenRouter;

namespace AIUsage.Core.Tests.Providers;

public class OpenRouterUsageMapperTests
{
    private static JsonElement Data(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void DataObject_ExtractsNestedDataProperty()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("""{ "data": { "total_usage": 5 } }""");
        var data = OpenRouterUsageMapper.DataObject(body);
        Assert.NotNull(data);
        Assert.Equal(5, data!.Value.GetProperty("total_usage").GetDouble());
    }

    [Fact]
    public void DataObject_MissingDataProperty_ReturnsNull()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{}");
        Assert.Null(OpenRouterUsageMapper.DataObject(body));
    }

    [Fact]
    public void CreditsLines_WithTotalCredits_ProducesProgressAndBalance()
    {
        var data = Data("""{ "total_usage": 30, "total_credits": 100 }""");

        var lines = OpenRouterUsageMapper.CreditsLines(data);

        var credits = Assert.IsType<MetricLine.Progress>(lines.Single(l => l.Label == "Credits"));
        Assert.Equal(30, credits.Used);
        Assert.Equal(100, credits.Limit);
        var balance = Assert.IsType<MetricLine.Values>(lines.Single(l => l.Label == "Balance"));
        Assert.Equal(70, balance.ValuesList[0].Number);
    }

    [Fact]
    public void CreditsLines_WithoutTotalCredits_OnlyProducesBalance()
    {
        var data = Data("""{ "total_usage": 30 }""");

        var lines = OpenRouterUsageMapper.CreditsLines(data);

        Assert.DoesNotContain(lines, l => l.Label == "Credits");
        var balance = Assert.IsType<MetricLine.Values>(lines.Single(l => l.Label == "Balance"));
        Assert.Equal(0, balance.ValuesList[0].Number); // max(0, 0 - 30)
    }

    [Fact]
    public void CreditsLines_MissingTotalUsage_ReturnsEmpty()
    {
        var data = Data("{}");
        Assert.Empty(OpenRouterUsageMapper.CreditsLines(data));
    }

    [Fact]
    public void KeyMetrics_DailyWeeklyMonthlySpend_ProduceValuesLines()
    {
        var data = Data("""
        { "usage_daily": 1.5, "usage_weekly": 10, "usage_monthly": 40 }
        """);

        var (plan, lines) = OpenRouterUsageMapper.KeyMetrics(data);

        Assert.Null(plan);
        Assert.Equal(1.5, Assert.IsType<MetricLine.Values>(lines.Single(l => l.Label == "Today")).ValuesList[0].Number);
        Assert.Equal(10, Assert.IsType<MetricLine.Values>(lines.Single(l => l.Label == "This Week")).ValuesList[0].Number);
        Assert.Equal(40, Assert.IsType<MetricLine.Values>(lines.Single(l => l.Label == "This Month")).ValuesList[0].Number);
    }

    [Fact]
    public void KeyMetrics_KeyLimitPresent_ProducesProgressLine()
    {
        var data = Data("""{ "limit": 50, "usage": 20 }""");

        var (_, lines) = OpenRouterUsageMapper.KeyMetrics(data);

        var limit = Assert.IsType<MetricLine.Progress>(lines.Single(l => l.Label == "Key Limit"));
        Assert.Equal(20, limit.Used);
        Assert.Equal(50, limit.Limit);
    }

    [Fact]
    public void KeyMetrics_FreeTierTrue_ReportsFreeTierPlan()
    {
        var data = Data("""{ "is_free_tier": true }""");
        var (plan, _) = OpenRouterUsageMapper.KeyMetrics(data);
        Assert.Equal("Free tier", plan);
    }

    [Fact]
    public void KeyMetrics_FreeTierFalse_ReportsPayAsYouGoPlan()
    {
        var data = Data("""{ "is_free_tier": false }""");
        var (plan, _) = OpenRouterUsageMapper.KeyMetrics(data);
        Assert.Equal("Pay as you go", plan);
    }
}
