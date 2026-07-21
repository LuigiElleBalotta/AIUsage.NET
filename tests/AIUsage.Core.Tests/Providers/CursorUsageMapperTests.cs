using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Providers.Cursor;

namespace AIUsage.Core.Tests.Providers;

public class CursorPlanUsageFactsTests
{
    private static JsonElement Usage(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Facts_EnabledByDefault_WhenNoEnabledField()
    {
        var facts = new CursorPlanUsageFacts(Usage("{}"));
        Assert.True(facts.IsEnabled);
    }

    [Fact]
    public void Facts_ExplicitlyDisabled_IsNotEnabled()
    {
        var facts = new CursorPlanUsageFacts(Usage("""{ "enabled": false }"""));
        Assert.False(facts.IsEnabled);
    }

    [Fact]
    public void Facts_PlanUsageWithLimit_HasLimitAndUsage()
    {
        var facts = new CursorPlanUsageFacts(Usage("""
        { "planUsage": { "limit": 2000, "totalPercentUsed": 45 } }
        """));

        Assert.True(facts.HasPlanUsage);
        Assert.True(facts.HasLimit);
        Assert.Equal(2000, facts.Limit);
        Assert.Equal(45, facts.TotalPercentUsed);
        Assert.False(facts.PlanUsageLimitMissing);
        Assert.False(facts.ShouldTryGenericRequestFallback);
    }

    [Fact]
    public void Facts_PlanUsageWithoutLimitOrPercent_ShouldTryFallback()
    {
        var facts = new CursorPlanUsageFacts(Usage("""{ "planUsage": {} }"""));

        Assert.True(facts.HasPlanUsage);
        Assert.False(facts.HasLimit);
        Assert.True(facts.PlanUsageLimitMissing);
        Assert.True(facts.ShouldTryGenericRequestFallback);
    }

    [Fact]
    public void Facts_TeamByShape_WhenSpendLimitTypeIsTeam()
    {
        var facts = new CursorPlanUsageFacts(Usage("""
        { "spendLimitUsage": { "limitType": "TEAM", "pooledLimit": 0 } }
        """));

        Assert.True(facts.IsTeamByShape);
    }

    [Fact]
    public void Facts_TeamByShape_WhenPooledLimitPositive()
    {
        var facts = new CursorPlanUsageFacts(Usage("""
        { "spendLimitUsage": { "pooledLimit": 500 } }
        """));

        Assert.True(facts.IsTeamByShape);
    }

    [Fact]
    public void Facts_NotTeamByShape_WhenNoSpendLimitUsage()
    {
        var facts = new CursorPlanUsageFacts(Usage("{}"));
        Assert.False(facts.IsTeamByShape);
    }
}

public class CursorUsageMapperTests
{
    private static JsonElement Usage(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void MapUsage_PersonalAccount_UsesPercentProgress()
    {
        var usage = Usage("""
        {
          "planUsage": { "limit": 1000, "totalPercentUsed": 42 },
          "billingCycleStart": 1700000000000,
          "billingCycleEnd": 1702600000000
        }
        """);

        var mapped = CursorUsageMapper.MapUsage(usage, "pro", creditGrants: null, stripeBalanceCents: 0);

        var total = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Total usage"));
        Assert.Equal(42, total.Used);
        Assert.Equal(100, total.Limit);
        Assert.Equal(ProgressFormat.PercentValue, total.Format);
    }

    [Fact]
    public void MapUsage_TeamAccount_UsesDollarProgress()
    {
        var usage = Usage("""
        {
          "planUsage": { "limit": 5000, "totalSpend": 1200 },
          "spendLimitUsage": { "limitType": "team", "pooledLimit": 5000 }
        }
        """);

        var mapped = CursorUsageMapper.MapUsage(usage, "team", creditGrants: null, stripeBalanceCents: 0);

        var total = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Total usage"));
        Assert.Equal(ProgressFormat.DollarsValue, total.Format);
        Assert.Equal(12.0, total.Used); // 1200 cents -> $12
        Assert.Equal(50.0, total.Limit); // 5000 cents -> $50
    }

    [Fact]
    public void MapUsage_NoActiveSubscription_ThrowsWhenDisabled()
    {
        var usage = Usage("""{ "enabled": false }""");

        var ex = Assert.Throws<CursorUsageError>(() => CursorUsageMapper.MapUsage(usage, "pro", null, 0));
        Assert.Equal(CursorUsageErrorKind.NoActiveSubscription, ex.Kind);
    }

    [Fact]
    public void MapUsage_MissingTotalUsageLimit_Throws()
    {
        var usage = Usage("""{ "planUsage": {} }""");

        var ex = Assert.Throws<CursorUsageError>(() => CursorUsageMapper.MapUsage(usage, "pro", null, 0));
        Assert.Equal(CursorUsageErrorKind.TotalUsageLimitMissing, ex.Kind);
    }

    [Fact]
    public void MapUsage_WithCreditGrantsAndStripeBalance_AddsCreditsLine()
    {
        var usage = Usage("""{ "planUsage": { "limit": 1000, "totalPercentUsed": 10 } }""");
        var creditGrants = Usage("""{ "hasCreditGrants": true, "totalCents": 2000, "usedCents": 500 }""");

        var mapped = CursorUsageMapper.MapUsage(usage, "pro", creditGrants, stripeBalanceCents: 300);

        var credits = Assert.IsType<MetricLine.Values>(mapped.Lines.Single(l => l.Label == "Credits"));
        Assert.Equal(18.0, credits.ValuesList[0].Number); // (2000+300-500)/100 = 18
    }

    [Fact]
    public void ShouldUseRequestBasedFallback_EnterprisePlanUnusable_ReturnsTrue()
    {
        var usage = Usage("""{ "planUsage": {} }""");
        var (shouldFallback, message) = CursorUsageMapper.ShouldUseRequestBasedFallback(usage, "enterprise", planInfoUnavailable: false);
        Assert.True(shouldFallback);
        Assert.Contains("Enterprise", message);
    }

    [Fact]
    public void ShouldUseRequestBasedFallback_TeamPlanUnusable_ReturnsTrue()
    {
        var usage = Usage("""{ "planUsage": {} }""");
        var (shouldFallback, message) = CursorUsageMapper.ShouldUseRequestBasedFallback(usage, "team", planInfoUnavailable: false);
        Assert.True(shouldFallback);
        Assert.Contains("Team", message);
    }

    [Fact]
    public void ShouldUseRequestBasedFallback_Disabled_ReturnsFalse()
    {
        var usage = Usage("""{ "enabled": false }""");
        var (shouldFallback, _) = CursorUsageMapper.ShouldUseRequestBasedFallback(usage, "pro", planInfoUnavailable: true);
        Assert.False(shouldFallback);
    }

    [Fact]
    public void ShouldUseRequestBasedFallback_UsablePlanUsage_ReturnsFalse()
    {
        var usage = Usage("""{ "planUsage": { "limit": 1000, "totalPercentUsed": 10 } }""");
        var (shouldFallback, _) = CursorUsageMapper.ShouldUseRequestBasedFallback(usage, "pro", planInfoUnavailable: false);
        Assert.False(shouldFallback);
    }

    [Fact]
    public void MapRequestBasedUsage_Gpt4Present_ProducesRequestsLine()
    {
        var usage = Usage("""
        { "gpt-4": { "numRequests": 30, "maxRequestUsage": 500 }, "startOfMonth": "2026-07-01T00:00:00Z" }
        """);

        var mapped = CursorUsageMapper.MapRequestBasedUsage(usage, "pro", "unavailable");

        var requests = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Requests"));
        Assert.Equal(30, requests.Used);
        Assert.Equal(500, requests.Limit);
    }

    [Fact]
    public void MapRequestBasedUsage_NoUsableData_ThrowsRequestBasedUnavailable()
    {
        var ex = Assert.Throws<CursorUsageError>(() => CursorUsageMapper.MapRequestBasedUsage(null, "pro", "custom message"));
        Assert.Equal(CursorUsageErrorKind.RequestBasedUnavailable, ex.Kind);
        Assert.Equal("custom message", ex.Message);
    }

    [Theory]
    [InlineData("pro plan", "Pro Plan")]
    [InlineData("  team  ", "Team")]
    public void PlanLabel_TitleCasesTrimmedValue(string input, string expected)
    {
        Assert.Equal(expected, CursorUsageMapper.PlanLabel(input));
    }

    [Fact]
    public void PlanLabel_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(CursorUsageMapper.PlanLabel(null));
        Assert.Null(CursorUsageMapper.PlanLabel("   "));
    }
}
