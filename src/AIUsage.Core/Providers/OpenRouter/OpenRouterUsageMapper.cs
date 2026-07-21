using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenRouter;

/// <summary>Builds metric lines from OpenRouter's /credits and /key payloads. Direct port.</summary>
public static class OpenRouterUsageMapper
{
    public static JsonElement? DataObject(byte[] body)
    {
        var root = ProviderParse.JsonObject(body);
        if (root is not { } r || !r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        return data;
    }

    public static List<MetricLine> CreditsLines(JsonElement data)
    {
        if (ProviderParse.Number(GetOrNull(data, "total_usage")) is not { } totalUsage) return new List<MetricLine>();

        var used = Math.Max(0, totalUsage);
        var totalCredits = Math.Max(0, ProviderParse.Number(GetOrNull(data, "total_credits")) ?? 0);

        var lines = new List<MetricLine>();
        if (totalCredits > 0)
        {
            lines.Add(new MetricLine.Progress("Credits", used, totalCredits, ProgressFormat.DollarsValue));
        }
        lines.Add(new MetricLine.Values("Balance", new List<MetricValue> { new(Math.Max(0, totalCredits - used), MetricKind.Dollars) }));
        return lines;
    }

    public static (string? Plan, List<MetricLine> Lines) KeyMetrics(JsonElement data)
    {
        var lines = new List<MetricLine>();
        AppendSpend(GetOrNull(data, "usage_daily"), "Today", lines);
        AppendSpend(GetOrNull(data, "usage_weekly"), "This Week", lines);
        AppendSpend(GetOrNull(data, "usage_monthly"), "This Month", lines);

        if (ProviderParse.Number(GetOrNull(data, "limit")) is { } limit && limit > 0)
        {
            lines.Add(new MetricLine.Progress("Key Limit", Math.Max(0, ProviderParse.Number(GetOrNull(data, "usage")) ?? 0), limit, ProgressFormat.DollarsValue));
        }

        string? plan = null;
        if (data.TryGetProperty("is_free_tier", out var ft))
        {
            if (ft.ValueKind == JsonValueKind.True) plan = "Free tier";
            else if (ft.ValueKind == JsonValueKind.False) plan = "Pay as you go";
        }
        return (plan, lines);
    }

    private static void AppendSpend(JsonElement? value, string label, List<MetricLine> lines)
    {
        if (ProviderParse.Number(value) is not { } amount) return;
        lines.Add(new MetricLine.Values(label, new List<MetricValue> { new(Math.Max(0, amount), MetricKind.Dollars) }));
    }

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
