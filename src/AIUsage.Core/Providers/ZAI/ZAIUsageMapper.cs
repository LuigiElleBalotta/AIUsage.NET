using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.ZAI;

/// <summary>Builds metric lines from Z.ai's quota + subscription payloads. Direct port of ZAIUsageMapper.</summary>
public static class ZAIUsageMapper
{
    public const long MonthlyPeriodMs = 30L * 24 * 60 * 60 * 1000;

    public static (string? Plan, List<MetricLine> Lines) Map(byte[] quotaBody, byte[]? subscriptionBody)
    {
        var plan = subscriptionBody is not null ? PlanName(subscriptionBody) : null;
        var lines = MapQuota(quotaBody);
        return (plan, lines);
    }

    public static bool IsNoCodingPlan(byte[] body)
    {
        var root = ProviderParse.JsonObject(body);
        if (root is not { } r || !r.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.False) return false;
        var msg = r.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        return msg.ToLowerInvariant().Contains("coding plan");
    }

    public static List<MetricLine> MapQuota(byte[] body)
    {
        var root = ProviderParse.JsonObject(body) ?? throw new ZAIUsageError(ZAIUsageErrorKind.InvalidResponse);

        JsonElement container;
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind != JsonValueKind.Object) throw new ZAIUsageError(ZAIUsageErrorKind.InvalidResponse);
            container = data;
        }
        else
        {
            container = root;
        }
        if (!container.TryGetProperty("limits", out var limitsEl) || limitsEl.ValueKind != JsonValueKind.Array)
        {
            throw new ZAIUsageError(ZAIUsageErrorKind.InvalidResponse);
        }
        var limits = limitsEl.EnumerateArray().ToList();
        if (limits.Count == 0) return new List<MetricLine> { MetricLine.NoUsageData };

        var lines = new List<MetricLine>();
        var sawRecognizedLimit = false;

        var tokenLimits = limits.Where(l => GetString(l, "type") == "TOKENS_LIMIT" || GetString(l, "name") == "TOKENS_LIMIT").ToList();
        foreach (var entry in tokenLimits)
        {
            var window = ClassifyTokenWindow(entry);
            if (window is null) continue;
            sawRecognizedLimit = true;
            if (window.Value.IsSession) lines.Add(PercentLine(entry, "Session", window.Value.PeriodMs));
            else lines.Add(PercentLine(entry, "Weekly", window.Value.PeriodMs));
        }

        var web = FindLimit(limits, "TIME_LIMIT");
        if (web is { } webEntry)
        {
            sawRecognizedLimit = true;
            lines.Add(WebSearchLine(webEntry));
        }

        if (lines.Count == 0)
        {
            if (sawRecognizedLimit) throw new ZAIUsageError(ZAIUsageErrorKind.InvalidResponse);
            return new List<MetricLine> { MetricLine.NoUsageData };
        }
        return lines;
    }

    public static string? PlanName(byte[] body)
    {
        var root = ProviderParse.JsonObject(body);
        if (root is not { } r || !r.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var first = list.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return null;
        var name = GetString(first, "productName")?.NilIfEmpty();
        return name;
    }

    private static (bool IsSession, long PeriodMs)? ClassifyTokenWindow(JsonElement entry)
    {
        if (ProviderParse.Number(GetOrNull(entry, "unit")) is not { } unit || ProviderParse.Number(GetOrNull(entry, "number")) is not { } number || number <= 0)
        {
            throw new ZAIUsageError(ZAIUsageErrorKind.InvalidResponse);
        }
        double unitMs = unit switch
        {
            3 => 60L * 60 * 1000,
            4 => 24L * 60 * 60 * 1000,
            6 => 7L * 24 * 60 * 60 * 1000,
            5 => 30L * 24 * 60 * 60 * 1000,
            _ => -1
        };
        if (unitMs < 0) return null;
        var duration = unitMs * number;
        if (duration < 1 || duration >= (double)long.MaxValue) throw new ZAIUsageError(ZAIUsageErrorKind.InvalidResponse);
        var periodMs = (long)duration;
        return (periodMs < 24 * 60 * 60 * 1000, periodMs);
    }

    private static MetricLine PercentLine(JsonElement entry, string label, long periodMs)
    {
        if (ProviderParse.Number(GetOrNull(entry, "percentage")) is not { } rawPercentage)
        {
            throw new ZAIUsageError(ZAIUsageErrorKind.InvalidResponse);
        }
        var percentage = ProviderParse.ClampPercent(rawPercentage);
        var resetsAt = ProviderParse.Number(GetOrNull(entry, "nextResetTime")) is { } ms ? EpochMsToDate(ms) : (DateTimeOffset?)null;
        return new MetricLine.Progress(label, percentage, 100, ProgressFormat.PercentValue, resetsAt, periodMs);
    }

    private static MetricLine WebSearchLine(JsonElement entry)
    {
        if (ProviderParse.Number(GetOrNull(entry, "currentValue")) is not { } used || used < 0 ||
            ProviderParse.Number(GetOrNull(entry, "usage")) is not { } limit || limit < 0)
        {
            throw new ZAIUsageError(ZAIUsageErrorKind.InvalidResponse);
        }
        var resetsAt = ProviderParse.Number(GetOrNull(entry, "nextResetTime")) is { } ms ? EpochMsToDate(ms) : (DateTimeOffset?)null;
        return new MetricLine.Progress("Web Searches", used, limit, ProgressFormat.CountValue("searches"), resetsAt, MonthlyPeriodMs);
    }

    private static JsonElement? FindLimit(List<JsonElement> limits, string type)
    {
        foreach (var entry in limits)
        {
            if (GetString(entry, "type") == type || GetString(entry, "name") == type) return entry;
        }
        return null;
    }

    private static DateTimeOffset EpochMsToDate(double ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms);

    private static string? GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
