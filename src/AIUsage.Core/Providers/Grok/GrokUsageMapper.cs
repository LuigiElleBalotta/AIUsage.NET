using AIUsage.Core.Models;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Grok;

public sealed class GrokMappedUsage
{
    public List<MetricLine> Lines { get; set; } = new();
}

/// <summary>Maps Grok's credits-format billing response. Direct port of GrokUsageMapper.</summary>
public static class GrokUsageMapper
{
    public static GrokMappedUsage MapCreditsConfig(HttpResponseResult response)
    {
        ProviderAuthRetry.RequireSuccess(response, () => new GrokAuthError(GrokAuthErrorKind.Expired), status => new GrokUsageError(GrokUsageErrorKind.RequestFailed, status));
        var config = GrokCreditsConfigDecoder.Decode(response.Body);

        var lines = new List<MetricLine>();
        if (config.PeriodType == GrokCreditsConfigDecoder.WeeklyPeriodType)
        {
            lines.Add(new MetricLine.Progress("Weekly limit", ProviderParse.ClampPercent(config.UsedPercent), 100, ProgressFormat.PercentValue, config.PeriodEnd, config.PeriodDurationMs));
        }
        lines.Add(new MetricLine.Badge(
            "Pay as you go",
            config.OnDemandCap > 0 ? $"{FormatUnits(config.OnDemandCap)} cap" : "Disabled",
            config.OnDemandCap > 0 ? "#22c55e" : "#a3a3a3"));
        return new GrokMappedUsage { Lines = lines };
    }

    public static string? PlanName(HttpResponseResult response)
    {
        if (response.StatusCode is < 200 or >= 300) return null;
        var body = ProviderParse.JsonObject(response.Body);
        if (body is not { } b || !b.TryGetProperty("subscription_tier_display", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String) return null;
        var trimmed = v.GetString()?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string FormatUnits(double value)
    {
        return Math.Round(value) == value ? ((long)value).ToString() : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
