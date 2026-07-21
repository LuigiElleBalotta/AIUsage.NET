using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Providers.Devin;
using AIUsage.Core.Tests.TestHelpers;

namespace AIUsage.Core.Tests.Providers;

public class DevinUsageMapperTests
{
    private static JsonElement UserStatus(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void MapUserStatus_DailyAndWeeklyPresent_ProducesBothLines()
    {
        var status = UserStatus("""
        {
          "planStatus": {
            "planInfo": { "planName": "Core" },
            "dailyQuotaRemainingPercent": 70,
            "weeklyQuotaRemainingPercent": 40,
            "dailyQuotaResetAtUnix": 1000,
            "weeklyQuotaResetAtUnix": 2000
          }
        }
        """);

        var mapped = DevinUsageMapper.MapUserStatus(status);

        Assert.Equal("Core", mapped.Plan);
        var daily = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Daily quota"));
        Assert.Equal(30, daily.Used); // 100 - 70 remaining
        var weekly = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Weekly quota"));
        Assert.Equal(60, weekly.Used); // 100 - 40 remaining
    }

    [Fact]
    public void MapUserStatus_HideDailyQuota_ReassignsDailyToWeeklyLabel()
    {
        var status = UserStatus("""
        {
          "planStatus": {
            "planInfo": { "planName": "Core", "hideDailyQuota": true },
            "dailyQuotaRemainingPercent": 25
          }
        }
        """);

        var mapped = DevinUsageMapper.MapUserStatus(status);

        Assert.DoesNotContain(mapped.Lines, l => l.Label == "Daily quota");
        var weekly = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Weekly quota"));
        Assert.Equal(75, weekly.Used); // 100 - 25 remaining, from the daily field
    }

    [Fact]
    public void MapUserStatus_ExtraUsageBalanceFromMicros_ProducesValuesLine()
    {
        var status = UserStatus("""
        {
          "planStatus": {
            "planInfo": { "planName": "Core" },
            "dailyQuotaRemainingPercent": 50,
            "overageBalanceMicros": 5000000
          }
        }
        """);

        var mapped = DevinUsageMapper.MapUserStatus(status);

        var balance = Assert.IsType<MetricLine.Values>(mapped.Lines.Single(l => l.Label == "Extra usage balance"));
        Assert.Equal(5.0, balance.ValuesList[0].Number); // 5,000,000 micros -> $5
    }

    [Fact]
    public void MapUserStatus_NoLinesProducible_ThrowsQuotaUnavailable()
    {
        var status = UserStatus("""{ "planStatus": { "planInfo": { "planName": "Core" } } }""");

        var ex = Assert.Throws<DevinUsageError>(() => DevinUsageMapper.MapUserStatus(status));
        Assert.Equal(DevinUsageErrorKind.QuotaUnavailable, ex.Kind);
    }

    [Fact]
    public void MapUserStatusResponse_MalformedBody_ThrowsInvalidResponse()
    {
        var response = HttpResponseFixture.Json("""{ "foo": "bar" }""");
        var ex = Assert.Throws<DevinUsageError>(() => DevinUsageMapper.MapUserStatusResponse(response));
        Assert.Equal(DevinUsageErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public void MapUserStatusResponse_EmptyBody_ThrowsInvalidResponse()
    {
        var response = HttpResponseFixture.Empty(200);
        var ex = Assert.Throws<DevinUsageError>(() => DevinUsageMapper.MapUserStatusResponse(response));
        Assert.Equal(DevinUsageErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public void MapUserStatusResponse_ValidWrapper_DelegatesToMapUserStatus()
    {
        var response = HttpResponseFixture.Json("""
        {
          "userStatus": {
            "planStatus": {
              "planInfo": { "planName": "Team" },
              "weeklyQuotaRemainingPercent": 90
            }
          }
        }
        """);

        var mapped = DevinUsageMapper.MapUserStatusResponse(response);

        Assert.Equal("Team", mapped.Plan);
        var weekly = Assert.IsType<MetricLine.Progress>(mapped.Lines.Single(l => l.Label == "Weekly quota"));
        Assert.Equal(10, weekly.Used);
    }
}
