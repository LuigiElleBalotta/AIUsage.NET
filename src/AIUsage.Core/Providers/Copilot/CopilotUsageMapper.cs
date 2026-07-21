using System.Globalization;
using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Copilot;

public sealed class CopilotMappedUsage
{
    public string? Plan { get; set; }
    public List<MetricLine> Lines { get; set; } = new();
    public bool IsOrgManagedSeat { get; set; }
}

/// <summary>Normalizes the /copilot_internal/user response into meters. Direct port of CopilotUsageMapper.</summary>
public static class CopilotUsageMapper
{
    public static readonly long PeriodMs = MetricPeriod.MonthMs;

    public static CopilotMappedUsage Map(HttpResponseResult response)
    {
        var body = ProviderParse.JsonObject(response.Body) ?? throw new CopilotUsageError(CopilotUsageErrorKind.InvalidResponse);
        return Map(body);
    }

    public static CopilotMappedUsage Map(JsonElement body)
    {
        var plan = PlanLabel(GetString(body, "copilot_plan"));
        var resetsAt = ParseResetDate(GetString(body, "quota_reset_date")) ?? ParseResetDate(GetString(body, "limited_user_reset_date"));

        var lines = new List<MetricLine>();

        var snapshots = GetOrNull(body, "quota_snapshots");
        var premium = snapshots is { } s ? GetOrNull(s, "premium_interactions") : null;
        var creditsLine = SnapshotLine("Credits", premium, resetsAt);
        AppendIfPresent(lines, creditsLine);
        if (creditsLine is not null) AppendIfPresent(lines, OverageLine(premium));

        AppendIfPresent(lines, SnapshotLine("Chat", snapshots is { } s2 ? GetOrNull(s2, "chat") : null, resetsAt));
        AppendIfPresent(lines, SnapshotLine("Completions", snapshots is { } s3 ? GetOrNull(s3, "completions") : null, resetsAt));

        if (lines.Count == 0)
        {
            var limited = GetOrNull(body, "limited_user_quotas");
            var monthly = GetOrNull(body, "monthly_quotas");
            AppendIfPresent(lines, LimitedLine("Chat", limited is { } l1 ? GetOrNull(l1, "chat") : null, monthly is { } m1 ? GetOrNull(m1, "chat") : null, resetsAt));
            AppendIfPresent(lines, LimitedLine("Completions", limited is { } l2 ? GetOrNull(l2, "completions") : null, monthly is { } m2 ? GetOrNull(m2, "completions") : null, resetsAt));
        }

        if (lines.Count == 0)
        {
            if (ProviderParse.Bool(GetOrNull(body, "token_based_billing")) == true)
            {
                return new CopilotMappedUsage { Plan = plan, Lines = new List<MetricLine>(), IsOrgManagedSeat = true };
            }
            throw new CopilotUsageError(CopilotUsageErrorKind.QuotaUnavailable);
        }

        return new CopilotMappedUsage { Plan = plan, Lines = lines };
    }

    private static MetricLine? SnapshotLine(string label, JsonElement? raw, DateTimeOffset? resetsAt)
    {
        if (raw is not { } snapshot || snapshot.ValueKind != JsonValueKind.Object) return null;

        var entitlement = ProviderParse.Number(GetOrNull(snapshot, "entitlement"));
        var remaining = ProviderParse.Number(GetOrNull(snapshot, "remaining"));

        if (ProviderParse.Bool(GetOrNull(snapshot, "unlimited")) == true || entitlement == -1 || remaining == -1) return null;
        if (entitlement == 0) return null;

        double usedPercent;
        if (ProviderParse.Number(GetOrNull(snapshot, "percent_remaining")) is { } percentRemaining)
        {
            usedPercent = ProviderParse.ClampPercent(100 - percentRemaining);
        }
        else if (entitlement is { } e && e > 0 && remaining is { } r)
        {
            usedPercent = ProviderParse.ClampPercent(100 - (r / e) * 100);
        }
        else
        {
            return null;
        }

        return new MetricLine.Progress(label, usedPercent, 100, ProgressFormat.PercentValue, resetsAt, PeriodMs);
    }

    private static MetricLine? OverageLine(JsonElement? raw)
    {
        if (raw is not { } snapshot || snapshot.ValueKind != JsonValueKind.Object) return null;
        if (ProviderParse.Bool(GetOrNull(snapshot, "overage_permitted")) != true) return null;
        var overage = Math.Max(0, ProviderParse.Number(GetOrNull(snapshot, "overage_count")) ?? 0);
        return new MetricLine.Values("Extra Usage", new List<MetricValue> { new(overage, MetricKind.Count) });
    }

    private static MetricLine? LimitedLine(string label, JsonElement? remaining, JsonElement? total, DateTimeOffset? resetsAt)
    {
        if (ProviderParse.Number(total) is not { } totalVal || totalVal <= 0) return null;
        if (ProviderParse.Number(remaining) is not { } remainingVal) return null;
        var used = Math.Max(0, totalVal - remainingVal);
        return new MetricLine.Progress(label, ProviderParse.ClampPercent((used / totalVal) * 100), 100, ProgressFormat.PercentValue, resetsAt, PeriodMs);
    }

    private static void AppendIfPresent(List<MetricLine> lines, MetricLine? line)
    {
        if (line is not null) lines.Add(line);
    }

    private static string? PlanLabel(string? value)
    {
        var raw = value?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.TitleCased(c => c is '_' or ' ' or '-', lowercasingTail: true);
    }

    private static DateTimeOffset? ParseResetDate(string? value)
    {
        var raw = value?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (AIUsageISO8601.Parse(raw) is { } date) return date;
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
        {
            return new DateTimeOffset(d, TimeSpan.Zero);
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
