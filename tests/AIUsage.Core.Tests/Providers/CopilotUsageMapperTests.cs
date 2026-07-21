using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Providers.Copilot;
using AIUsage.Core.Tests.TestHelpers;

namespace AIUsage.Core.Tests.Providers;

public class CopilotUsageMapperTests
{
    [Fact]
    public void Map_PremiumInteractions_ProducesCreditsAndOverageLines()
    {
        var response = HttpResponseFixture.Json("""
        {
          "copilot_plan": "individual_pro",
          "quota_reset_date": "2026-08-01T00:00:00Z",
          "quota_snapshots": {
            "premium_interactions": {
              "entitlement": 300,
              "remaining": 120,
              "overage_permitted": true,
              "overage_count": 15
            }
          }
        }
        """);

        var mapped = CopilotUsageMapper.Map(response);

        Assert.Equal("Individual Pro", mapped.Plan);
        var credits = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Credits"));
        Assert.Equal(60, credits.Used); // 100 - (120/300*100) = 60
        var overage = Assert.IsType<MetricLine.Values>(mapped.Lines.Single(l => l.Label == "Extra Usage"));
        Assert.Equal(15, overage.ValuesList[0].Number);
    }

    [Fact]
    public void Map_ChatAndCompletionsSnapshots_ProduceProgressLines()
    {
        var response = HttpResponseFixture.Json("""
        {
          "quota_snapshots": {
            "chat": { "entitlement": 100, "remaining": 80 },
            "completions": { "entitlement": 50, "remaining": 50 }
          }
        }
        """);

        var mapped = CopilotUsageMapper.Map(response);

        var chat = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Chat"));
        Assert.Equal(20, chat.Used);
        var completions = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Completions"));
        Assert.Equal(0, completions.Used);
    }

    [Fact]
    public void Map_UnlimitedSnapshot_IsSkipped()
    {
        // "chat" is unlimited and skipped; "completions" remains a usable line so Map doesn't throw.
        var response = HttpResponseFixture.Json("""
        {
          "quota_snapshots": {
            "chat": { "unlimited": true, "entitlement": -1, "remaining": -1 },
            "completions": { "entitlement": 100, "remaining": 90 }
          }
        }
        """);

        var mapped = CopilotUsageMapper.Map(response);

        Assert.False(mapped.IsOrgManagedSeat);
        Assert.DoesNotContain(mapped.Lines, l => l.Label == "Chat");
        Assert.Contains(mapped.Lines, l => l.Label == "Completions");
    }

    [Fact]
    public void Map_EntitlementMinusOne_IsSkipped()
    {
        var response = HttpResponseFixture.Json("""
        {
          "quota_snapshots": {
            "chat": { "entitlement": -1, "remaining": 10 },
            "completions": { "entitlement": 100, "remaining": 90 }
          }
        }
        """);
        var mapped = CopilotUsageMapper.Map(response);
        Assert.DoesNotContain(mapped.Lines, l => l.Label == "Chat");
        Assert.Contains(mapped.Lines, l => l.Label == "Completions");
    }

    [Fact]
    public void Map_NoPremiumSnapshots_FallsBackToLimitedUserQuotas()
    {
        var response = HttpResponseFixture.Json("""
        {
          "limited_user_quotas": { "chat": 30 },
          "monthly_quotas": { "chat": 100 }
        }
        """);

        var mapped = CopilotUsageMapper.Map(response);

        var chat = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Chat"));
        Assert.Equal(70, chat.Used); // used = 100 - 30 = 70, percent = 70/100*100
    }

    [Fact]
    public void Map_TokenBasedBillingWithNoLines_MarksOrgManagedSeat()
    {
        var response = HttpResponseFixture.Json("""{ "token_based_billing": true }""");

        var mapped = CopilotUsageMapper.Map(response);

        Assert.True(mapped.IsOrgManagedSeat);
        Assert.Empty(mapped.Lines);
    }

    [Fact]
    public void Map_NoLinesAndNotOrgManaged_ThrowsQuotaUnavailable()
    {
        var response = HttpResponseFixture.Json("{}");

        var ex = Assert.Throws<CopilotUsageError>(() => CopilotUsageMapper.Map(response));
        Assert.Equal(CopilotUsageErrorKind.QuotaUnavailable, ex.Kind);
    }

    [Fact]
    public void Map_EmptyBody_ThrowsInvalidResponse()
    {
        var response = HttpResponseFixture.Empty(200);
        var ex = Assert.Throws<CopilotUsageError>(() => CopilotUsageMapper.Map(response));
        Assert.Equal(CopilotUsageErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public void Map_PercentRemainingPresent_TakesPrecedenceOverRatio()
    {
        var response = HttpResponseFixture.Json("""
        { "quota_snapshots": { "chat": { "entitlement": 100, "remaining": 100, "percent_remaining": 10 } } }
        """);

        var mapped = CopilotUsageMapper.Map(response);

        var chat = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Chat"));
        Assert.Equal(90, chat.Used); // 100 - percent_remaining(10)
    }
}
