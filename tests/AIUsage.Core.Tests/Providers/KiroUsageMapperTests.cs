using AIUsage.Core.Models;
using AIUsage.Core.Providers.Kiro;
using AIUsage.Core.Tests.TestHelpers;

namespace AIUsage.Core.Tests.Providers;

public class KiroUsageMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MapUsageResponse_RequestsBreakdown_ProducesProgressLine()
    {
        var json = """
        {
          "usageBreakdownList": [
            { "resourceType": "AGENTIC_REQUEST", "currentUsage": 2650, "usageLimit": 2000, "unit": "REQUEST" }
          ],
          "nextDateReset": 1785628800,
          "subscriptionInfo": { "subscriptionTitle": "KIRO PRO+" }
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        Assert.Equal("KIRO PRO+", mapped.Plan);
        var requests = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == KiroUsageMapper.RequestsLabel));
        Assert.Equal(2650, requests.Used);
        Assert.Equal(2000, requests.Limit);
        Assert.Equal(ProgressFormat.CountValue("requests"), requests.Format);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785628800), requests.ResetsAt);
    }

    [Fact]
    public void MapUsageResponse_PercentageUnit_ProducesPercentFormat()
    {
        var json = """
        { "usageBreakdownList": [ { "currentUsage": 42, "usageLimit": 100, "unit": "PERCENTAGE" } ] }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        var usage = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Usage"));
        Assert.Equal(ProgressFormat.PercentValue, usage.Format);
    }

    [Fact]
    public void MapUsageResponse_Bonuses_ProducesOneLinePerBonus()
    {
        var json = """
        {
          "usageBreakdownList": [
            {
              "currentUsage": 0, "usageLimit": 50, "unit": "REQUEST",
              "bonuses": [
                { "displayName": "Welcome credits", "currentUsage": 10, "usageLimit": 100, "expiresAt": 1785628800 }
              ]
            }
          ]
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        var bonus = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Welcome credits"));
        Assert.Equal(10, bonus.Used);
        Assert.Equal(100, bonus.Limit);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785628800), bonus.ResetsAt);
    }

    [Fact]
    public void MapUsageResponse_BonusWithoutDisplayName_UsesDefaultLabel()
    {
        var json = """
        { "usageBreakdownList": [ { "currentUsage": 0, "usageLimit": 50, "bonuses": [ { "currentUsage": 1, "usageLimit": 10 } ] } ] }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        Assert.Contains(mapped.Lines, l => l.Label == KiroUsageMapper.BonusLabel);
    }

    [Fact]
    public void MapUsageResponse_OverageEnabledWithCap_PricesUnitsByRate()
    {
        // Regression test for a unit-conversion bug found against a real account:
        // currentOverages=740.1, overageRate=0.04 must price to $29.60 spent (740.1 * 0.04), not be
        // shown as "$740.10 spent" — currentOverages is a raw usage count, not already in dollars.
        var json = """
        {
          "overageConfiguration": { "overageStatus": "ENABLED" },
          "usageBreakdownList": [
            { "currentUsage": 0, "usageLimit": 50, "overageCap": 100, "overageRate": 0.04, "currentOverages": 740.1 }
          ]
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        var overage = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == KiroUsageMapper.OverageLabel));
        Assert.Equal(29.604, overage.Used, precision: 3);
        Assert.Equal(100, overage.Limit);
        Assert.Equal(ProgressFormat.DollarsValue, overage.Format);
    }

    [Fact]
    public void MapUsageResponse_OverageEnabledWithoutCap_ProducesPricedSpentBadge()
    {
        var json = """
        {
          "overageConfiguration": { "overageStatus": "ENABLED" },
          "usageBreakdownList": [ { "currentUsage": 0, "usageLimit": 50, "overageRate": 0.04, "currentOverages": 175 } ]
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        var badge = Assert.IsType<MetricLine.Badge>(mapped.Lines.Single(l => l.Label == KiroUsageMapper.OverageLabel));
        Assert.Equal("$7.00 spent", badge.BadgeText);
        Assert.Equal("#22c55e", badge.ColorHex);
    }

    [Fact]
    public void MapUsageResponse_OverageEnabledWithoutRate_CannotPriceUnits_ShowsEnabledBadge()
    {
        var json = """
        {
          "overageConfiguration": { "overageStatus": "ENABLED" },
          "usageBreakdownList": [ { "currentUsage": 0, "usageLimit": 50, "currentOverages": 175 } ]
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        var badge = Assert.IsType<MetricLine.Badge>(mapped.Lines.Single(l => l.Label == KiroUsageMapper.OverageLabel));
        Assert.Equal("Enabled", badge.BadgeText);
    }

    [Fact]
    public void MapUsageResponse_OverageDisabled_ProducesDisabledBadge()
    {
        var json = """
        {
          "overageConfiguration": { "overageStatus": "DISABLED" },
          "usageBreakdownList": [ { "currentUsage": 0, "usageLimit": 50, "overageCap": 20, "overageRate": 0.04, "currentOverages": 0 } ]
        }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        var badge = Assert.IsType<MetricLine.Badge>(mapped.Lines.Single(l => l.Label == KiroUsageMapper.OverageLabel));
        Assert.Equal("Disabled", badge.BadgeText);
        Assert.Equal("#a3a3a3", badge.ColorHex);
    }

    [Fact]
    public void MapUsageResponse_NoOverageFieldsAtAll_OmitsOverageLine()
    {
        var json = """
        { "usageBreakdownList": [ { "currentUsage": 0, "usageLimit": 50 } ] }
        """;
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        Assert.DoesNotContain(mapped.Lines, l => l.Label == KiroUsageMapper.OverageLabel);
    }

    [Fact]
    public void MapUsageResponse_MissingUsageOrLimit_SkipsBreakdown()
    {
        var json = """{ "usageBreakdownList": [ { "currentUsage": 10 } ] }""";
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        Assert.Empty(mapped.Lines);
    }

    [Fact]
    public void MapUsageResponse_ZeroLimit_SkipsBreakdown()
    {
        var json = """{ "usageBreakdownList": [ { "currentUsage": 0, "usageLimit": 0 } ] }""";
        var response = HttpResponseFixture.Json(json);

        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);

        Assert.Empty(mapped.Lines);
    }

    [Fact]
    public void MapUsageResponse_NoSubscriptionInfo_PlanIsNull()
    {
        var response = HttpResponseFixture.Json("{}");
        var mapped = KiroUsageMapper.MapUsageResponse(response, Now);
        Assert.Null(mapped.Plan);
    }

    [Fact]
    public void MapUsageResponse_Unauthorized_ThrowsAuthError()
    {
        var response = HttpResponseFixture.Json("{}", statusCode: 401);
        Assert.Throws<KiroAuthError>(() => KiroUsageMapper.MapUsageResponse(response, Now));
    }

    [Fact]
    public void MapUsageResponse_ServerError_ThrowsUsageError()
    {
        var response = HttpResponseFixture.Json("{}", statusCode: 500);
        Assert.Throws<KiroUsageError>(() => KiroUsageMapper.MapUsageResponse(response, Now));
    }
}
