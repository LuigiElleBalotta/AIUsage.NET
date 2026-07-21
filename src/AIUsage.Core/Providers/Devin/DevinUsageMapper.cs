using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Devin;

public sealed class DevinMappedUsage
{
    public string? Plan { get; set; }
    public List<MetricLine> Lines { get; set; } = new();
}

public enum DevinUsageErrorKind
{
    InvalidResponse,
    QuotaUnavailable
}

public sealed class DevinUsageError : Exception, Models.ICategorizedError
{
    public DevinUsageErrorKind Kind { get; }

    public DevinUsageError(DevinUsageErrorKind kind) : base("Devin quota data unavailable. Try again later.")
    {
        Kind = kind;
    }

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        DevinUsageErrorKind.InvalidResponse => Models.ErrorCategory.Decoding,
        DevinUsageErrorKind.QuotaUnavailable => Models.ErrorCategory.NotAvailable,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>Maps Devin's GetUserStatus response. Direct port of DevinUsageMapper.</summary>
public static class DevinUsageMapper
{
    public static readonly long DayPeriodMs = MetricPeriod.DayMs;
    public static readonly long WeekPeriodMs = MetricPeriod.WeekMs;

    public static DevinMappedUsage MapUserStatusResponse(HttpResponseResult response)
    {
        var body = ProviderParse.JsonObject(response.Body) ?? throw new DevinUsageError(DevinUsageErrorKind.InvalidResponse);
        if (!body.TryGetProperty("userStatus", out var userStatus) || userStatus.ValueKind != JsonValueKind.Object)
        {
            throw new DevinUsageError(DevinUsageErrorKind.InvalidResponse);
        }
        return MapUserStatus(userStatus);
    }

    public static DevinMappedUsage MapUserStatus(JsonElement userStatus)
    {
        var planStatus = GetOrNull(userStatus, "planStatus") ?? default;
        var planInfo = planStatus.ValueKind == JsonValueKind.Object ? GetOrNull(planStatus, "planInfo") ?? default : default;
        var plan = ReadTrimmedString(planInfo, "planName") ?? "Unknown";
        var hideDailyQuota = planInfo.ValueKind == JsonValueKind.Object && ProviderParse.Bool(GetOrNull(planInfo, "hideDailyQuota")) == true;

        var dailyRemaining = ProviderParse.Number(GetOrNull(planStatus, "dailyQuotaRemainingPercent"));
        var weeklyRemaining = ProviderParse.Number(GetOrNull(planStatus, "weeklyQuotaRemainingPercent"));
        var dailyReset = hideDailyQuota ? null : UnixSecondsToDate(GetOrNull(planStatus, "dailyQuotaResetAtUnix"));
        var weeklyReset = UnixSecondsToDate(GetOrNull(planStatus, "weeklyQuotaResetAtUnix"));
        var extraUsageBalance = DollarsFromMicros(GetOrNull(planStatus, "overageBalanceMicros"));

        var lines = new List<MetricLine>();
        if (!hideDailyQuota && dailyRemaining is { } dr)
        {
            lines.Add(QuotaLine("Daily quota", dr, dailyReset, DayPeriodMs));
        }

        if (weeklyRemaining is { } wr)
        {
            lines.Add(QuotaLine("Weekly quota", wr, weeklyReset, WeekPeriodMs));
        }
        else if (hideDailyQuota && dailyRemaining is { } dr2)
        {
            lines.Add(QuotaLine("Weekly quota", dr2, weeklyReset, WeekPeriodMs));
        }

        if (extraUsageBalance is { } balance)
        {
            lines.Add(new MetricLine.Values("Extra usage balance", new List<MetricValue> { new(balance, MetricKind.Dollars) }));
        }

        if (lines.Count == 0) throw new DevinUsageError(DevinUsageErrorKind.QuotaUnavailable);
        return new DevinMappedUsage { Plan = plan, Lines = lines };
    }

    private static MetricLine QuotaLine(string label, double remaining, DateTimeOffset? resetsAt, long periodDurationMs) =>
        new MetricLine.Progress(label, ProviderParse.ClampPercent(100 - remaining), 100, ProgressFormat.PercentValue, resetsAt, periodDurationMs);

    private static DateTimeOffset? UnixSecondsToDate(JsonElement? value) =>
        ProviderParse.Number(value) is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;

    private static double? DollarsFromMicros(JsonElement? value)
    {
        if (ProviderParse.Number(value) is not { } micros) return null;
        return Math.Max(0, micros) / 1_000_000;
    }

    private static string? ReadTrimmedString(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var trimmed = v.GetString()?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
