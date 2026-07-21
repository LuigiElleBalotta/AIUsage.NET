using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Cursor;

/// <summary>Combines Cursor Enterprise/team REST payloads. Direct port of CursorUsageSummaryMapper.</summary>
public static class CursorUsageSummaryMapper
{
    private sealed record BillingCycle(DateTimeOffset? ResetsAt, long PeriodDurationMs);

    public static bool HasUsableSummaryPayload(JsonElement summary)
    {
        var start = GetString(summary, "billingCycleStart") is { } s1 ? AIUsageISO8601.Parse(s1) : null;
        var end = GetString(summary, "billingCycleEnd") is { } s2 ? AIUsageISO8601.Parse(s2) : null;
        if (start is not null && end is not null && end > start) return true;

        var individual = GetOrNull(summary, "individualUsage");
        var team = GetOrNull(summary, "teamUsage");
        var plan = individual is { } i ? GetOrNull(i, "plan") : null;
        if (plan is { } p && new[] { "totalPercentUsed", "autoPercentUsed", "apiPercentUsed" }.Any(k => ProviderParse.Number(GetOrNull(p, k)) is not null))
        {
            return true;
        }
        return new[]
        {
            individual is { } i2 ? GetOrNull(i2, "onDemand") : null,
            individual is { } i3 ? GetOrNull(i3, "overall") : null,
            team is { } t1 ? GetOrNull(t1, "onDemand") : null,
            team is { } t2 ? GetOrNull(t2, "pooled") : null
        }.Any(UsableDollarBucket);
    }

    public static bool HasUsableRequestPayload(JsonElement usage)
    {
        if (GetOrNull(usage, "gpt-4") is { } requests && ProviderParse.Number(GetOrNull(requests, "maxRequestUsage")) is { } limit && limit > 0) return true;
        return GetString(usage, "startOfMonth") is { } s && AIUsageISO8601.Parse(s) is not null;
    }

    public static CursorMappedUsage Map(JsonElement? summary, JsonElement? requestUsage, string? planName, string unavailableMessage)
    {
        var cycle = BillingCycleOf(summary, requestUsage);
        var lines = new List<MetricLine>();

        var hasRequests = AppendRequests(requestUsage, cycle, lines);
        if (!hasRequests) AppendSummaryTotal(summary, cycle, lines);
        AppendStructuredPercentages(summary, cycle, lines);
        AppendOnDemand(summary, cycle, lines);

        if (lines.Count == 0) throw new CursorUsageError(CursorUsageErrorKind.RequestBasedUnavailable, message: unavailableMessage);

        var membershipType = summary is { } s ? GetString(s, "membershipType") : null;
        return new CursorMappedUsage { Plan = CursorUsageMapper.PlanLabel(planName) ?? CursorUsageMapper.PlanLabel(membershipType), Lines = lines };
    }

    private static bool AppendRequests(JsonElement? usage, BillingCycle cycle, List<MetricLine> lines)
    {
        if (usage is not { } u || GetOrNull(u, "gpt-4") is not { } requests ||
            ProviderParse.Number(GetOrNull(requests, "maxRequestUsage")) is not { } limit || limit <= 0)
        {
            return false;
        }
        var used = Math.Max(0, ProviderParse.Number(GetOrNull(requests, "numRequests")) ?? ProviderParse.Number(GetOrNull(requests, "numRequestsTotal")) ?? 0);
        foreach (var label in new[] { "Total usage", "Requests" })
        {
            lines.Add(new MetricLine.Progress(label, used, limit, ProgressFormat.CountValue("requests"), cycle.ResetsAt, cycle.PeriodDurationMs));
        }
        return true;
    }

    private static void AppendSummaryTotal(JsonElement? summary, BillingCycle cycle, List<MetricLine> lines)
    {
        if (summary is not { } s) return;
        var individual = GetOrNull(s, "individualUsage");
        var team = GetOrNull(s, "teamUsage");
        var limitType = GetString(s, "limitType")?.ToLowerInvariant();

        if (limitType == "team" && team is { } t && DollarMeter(GetOrNull(t, "pooled")) is { } pooled1)
        {
            AppendDollarProgress(pooled1, "Total usage", cycle, lines);
            return;
        }
        if (individual is { } i && GetOrNull(i, "plan") is { } plan && ProviderParse.Number(GetOrNull(plan, "totalPercentUsed")) is { } percent)
        {
            lines.Add(new MetricLine.Progress("Total usage", percent, 100, ProgressFormat.PercentValue, cycle.ResetsAt, cycle.PeriodDurationMs));
            return;
        }
        if (individual is { } i2 && DollarMeter(GetOrNull(i2, "overall")) is { } overall)
        {
            AppendDollarProgress(overall, "Total usage", cycle, lines);
            return;
        }
        if (team is { } t2 && DollarMeter(GetOrNull(t2, "pooled")) is { } pooled2)
        {
            AppendDollarProgress(pooled2, "Total usage", cycle, lines);
        }
    }

    private static void AppendStructuredPercentages(JsonElement? summary, BillingCycle cycle, List<MetricLine> lines)
    {
        if (summary is not { } s) return;
        var individual = GetOrNull(s, "individualUsage");
        var plan = individual is { } i ? GetOrNull(i, "plan") : null;
        if (plan is not { } p) return;
        foreach (var (key, label) in new[] { ("autoPercentUsed", "Auto usage"), ("apiPercentUsed", "API usage") })
        {
            if (ProviderParse.Number(GetOrNull(p, key)) is not { } percent) continue;
            lines.Add(new MetricLine.Progress(label, percent, 100, ProgressFormat.PercentValue, cycle.ResetsAt, cycle.PeriodDurationMs));
        }
    }

    private static void AppendOnDemand(JsonElement? summary, BillingCycle cycle, List<MetricLine> lines)
    {
        if (summary is not { } s) return;
        var individual = GetOrNull(s, "individualUsage");
        var team = GetOrNull(s, "teamUsage");
        if (individual is { } i && AppendOnDemandBucket(GetOrNull(i, "onDemand"), cycle, lines)) return;
        if (team is { } t) AppendOnDemandBucket(GetOrNull(t, "onDemand"), cycle, lines);
    }

    private static bool AppendOnDemandBucket(JsonElement? value, BillingCycle cycle, List<MetricLine> lines)
    {
        if (value is not { } bucket || bucket.ValueKind != JsonValueKind.Object) return false;
        if (bucket.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) return false;

        if (DollarMeter(bucket) is { } meter)
        {
            AppendDollarProgress(meter, "On-demand", cycle, lines);
            return true;
        }
        if (ProviderParse.Number(GetOrNull(bucket, "used")) is { } usedCents && usedCents > 0)
        {
            lines.Add(new MetricLine.Values("On-demand", new List<MetricValue> { new(ProviderParse.CentsToDollars(usedCents), MetricKind.Dollars) }));
            return true;
        }
        return false;
    }

    private static (double Used, double Limit)? DollarMeter(JsonElement? value)
    {
        if (value is not { } bucket || bucket.ValueKind != JsonValueKind.Object) return null;
        if (bucket.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) return null;
        if (ProviderParse.Number(GetOrNull(bucket, "limit")) is not { } limit || limit <= 0) return null;
        var reportedUsed = ProviderParse.Number(GetOrNull(bucket, "used"));
        var inferredUsed = Math.Max(0, limit - (ProviderParse.Number(GetOrNull(bucket, "remaining")) ?? limit));
        var used = (reportedUsed is { } r && r > 0) ? r : inferredUsed;
        return (Math.Max(0, used), limit);
    }

    private static bool UsableDollarBucket(JsonElement? value)
    {
        if (value is not { } bucket || bucket.ValueKind != JsonValueKind.Object) return false;
        if (bucket.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) return false;
        return DollarMeter(bucket) is not null || (ProviderParse.Number(GetOrNull(bucket, "used")) ?? 0) > 0;
    }

    private static void AppendDollarProgress((double Used, double Limit) meter, string label, BillingCycle cycle, List<MetricLine> lines)
    {
        lines.Add(new MetricLine.Progress(label, ProviderParse.CentsToDollars(meter.Used), ProviderParse.CentsToDollars(meter.Limit), ProgressFormat.DollarsValue, cycle.ResetsAt, cycle.PeriodDurationMs));
    }

    private static BillingCycle BillingCycleOf(JsonElement? summary, JsonElement? requestUsage)
    {
        var start = summary is { } s && GetString(s, "billingCycleStart") is { } s1 ? AIUsageISO8601.Parse(s1) : null;
        var end = summary is { } s2 && GetString(s2, "billingCycleEnd") is { } s3 ? AIUsageISO8601.Parse(s3) : null;
        if (start is not null && end is not null && end > start)
        {
            return new BillingCycle(end, (long)(end.Value - start.Value).TotalMilliseconds);
        }
        var requestStart = requestUsage is { } ru && GetString(ru, "startOfMonth") is { } s4 ? AIUsageISO8601.Parse(s4) : null;
        return new BillingCycle(requestStart?.AddMilliseconds(CursorUsageMapper.BillingPeriodMs), CursorUsageMapper.BillingPeriodMs);
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
