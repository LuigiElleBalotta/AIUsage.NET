using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Kiro;

public sealed record KiroMappedUsage(string? Plan, List<MetricLine> Lines);

/// <summary>
/// Maps a `getUsageLimits` response into the app's metric vocabulary. Reverse-engineered response
/// shape (undocumented, may change without notice):
///
/// {
///   "usageBreakdownList": [{ "resourceType", "currentUsage", "usageLimit", "unit", "bonuses": [...],
///                            "overageCap", "overageRate", "currentOverages" }],
///   "nextDateReset": <epoch seconds>,
///   "overageConfiguration": { "overageStatus" },
///   "subscriptionInfo": { "subscriptionTitle", "subscriptionType", "status", "overageCapability" },
///   "userInfo": { "email", "userId" }
/// }
///
/// `resourceType` is normally `AGENTIC_REQUEST` (the credits/requests meter this provider tracks).
/// `unit` is typically `"REQUEST"` or `"PERCENTAGE"`; anything else falls back to a plain count.
/// The overage fields live on the same breakdown entries as `currentUsage`/`usageLimit` — they price
/// what happens once that limit is exceeded, if the account has pay-as-you-go overage turned on.
/// </summary>
public static class KiroUsageMapper
{
    public const string RequestsLabel = "Requests";
    public const string BonusLabel = "Bonus Credits";
    public const string OverageLabel = "Overage";

    public static KiroMappedUsage MapUsageResponse(HttpResponseResult response, DateTimeOffset now)
    {
        ProviderAuthRetry.RequireSuccess(
            response,
            () => new KiroAuthError(KiroAuthErrorKind.SessionExpired),
            statusCode => new KiroUsageError(KiroUsageErrorKind.RequestFailed, statusCode));

        var root = ProviderParse.JsonObject(response.Body) ?? throw new KiroUsageError(KiroUsageErrorKind.InvalidResponse);
        return MapUsageResponse(root, now);
    }

    public static KiroMappedUsage MapUsageResponse(JsonElement root, DateTimeOffset now)
    {
        var lines = new List<MetricLine>();
        var plan = PlanTitle(root);
        var resetsAt = root.TryGetProperty("nextDateReset", out var resetEl) ? EpochSeconds(resetEl) : null;

        var overageStatus = OverageStatus(root);

        if (root.TryGetProperty("usageBreakdownList", out var breakdownEl) && breakdownEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var breakdown in breakdownEl.EnumerateArray())
            {
                var line = MapBreakdown(breakdown, resetsAt);
                if (line is not null) lines.Add(line);

                if (breakdown.TryGetProperty("bonuses", out var bonusesEl) && bonusesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bonus in bonusesEl.EnumerateArray())
                    {
                        var bonusLine = MapBonus(bonus);
                        if (bonusLine is not null) lines.Add(bonusLine);
                    }
                }

                var overageLine = MapOverage(breakdown, overageStatus);
                if (overageLine is not null) lines.Add(overageLine);
            }
        }

        return new KiroMappedUsage(plan, lines);
    }

    private static MetricLine? MapBreakdown(JsonElement breakdown, DateTimeOffset? resetsAt)
    {
        var used = ProviderParse.Number(GetOrNull(breakdown, "currentUsage"));
        var limit = ProviderParse.Number(GetOrNull(breakdown, "usageLimit"));
        if (used is null || limit is null || limit <= 0) return null;

        var unit = GetString(breakdown, "unit");
        var format = unit is "PERCENTAGE" or "PERCENT"
            ? ProgressFormat.PercentValue
            : ProgressFormat.CountValue(RequestSuffix(unit));

        var label = format is ProgressFormat.Percent ? "Usage" : RequestsLabel;
        return new MetricLine.Progress(label, used.Value, limit.Value, format, ResetsAt: resetsAt);
    }

    private static MetricLine? MapBonus(JsonElement bonus)
    {
        var used = ProviderParse.Number(GetOrNull(bonus, "currentUsage"));
        var limit = ProviderParse.Number(GetOrNull(bonus, "usageLimit"));
        if (used is null || limit is null || limit <= 0) return null;

        var expiresAt = bonus.TryGetProperty("expiresAt", out var expEl) ? EpochSeconds(expEl) : null;
        var displayName = GetString(bonus, "displayName");
        var label = string.IsNullOrEmpty(displayName) ? BonusLabel : displayName!;

        return new MetricLine.Progress(label, used.Value, limit.Value, ProgressFormat.CountValue("credits"), ResetsAt: expiresAt);
    }

    private static string? OverageStatus(JsonElement root)
    {
        if (!root.TryGetProperty("overageConfiguration", out var cfgEl) || cfgEl.ValueKind != JsonValueKind.Object) return null;
        return GetString(cfgEl, "overageStatus")?.ToUpperInvariant();
    }

    /// <summary>Overage pricing lives on the same breakdown entry as the base usage/limit — it only
    /// applies once that limit is exceeded, and only when the account has pay-as-you-go turned on.
    /// Skips entries that carry no overage fields at all (most accounts don't have overage capability).
    ///
    /// `currentOverages` is a raw usage count in the same unit as the base breakdown (e.g. requests
    /// past the included limit) — it is *unpriced*, despite its name suggesting an accrued dollar
    /// figure. Verified against a real account: `currentOverages=740.1`, `overageRate=0.04` matched
    /// a real billed overage of $29.60 (740.1 × 0.04 = 29.604), not $740.10. `overageCap` is the
    /// actual dollar cap. So the dollar amount spent is `currentOverages * overageRate`, never
    /// `currentOverages` alone.</summary>
    private static MetricLine? MapOverage(JsonElement breakdown, string? overageStatus)
    {
        var cap = ProviderParse.Number(GetOrNull(breakdown, "overageCap"));
        var rate = ProviderParse.Number(GetOrNull(breakdown, "overageRate"));
        var units = ProviderParse.Number(GetOrNull(breakdown, "currentOverages"));
        if (cap is null && rate is null && units is null) return null;

        if (overageStatus != "ENABLED")
        {
            return new MetricLine.Badge(OverageLabel, "Disabled", "#a3a3a3");
        }

        var spent = units is { } u && rate is { } r ? u * r : (double?)null;

        if (cap is { } capValue && capValue > 0 && spent is { } spentValue)
        {
            return new MetricLine.Progress(OverageLabel, spentValue, capValue, ProgressFormat.DollarsValue);
        }

        if (spent is { } spentOnly)
        {
            // Enabled but no declared cap: report the accrued spend as an unbounded balance instead
            // of a meter with nothing to divide by.
            return new MetricLine.Badge(OverageLabel, Formatters.Currency(spentOnly) + " spent", "#22c55e");
        }

        // Enabled, but missing the rate needed to price currentOverages into dollars — surface that
        // overage is on without fabricating a number from unpriced units.
        return new MetricLine.Badge(OverageLabel, "Enabled", "#22c55e");
    }

    private static string RequestSuffix(string? unit) => unit switch
    {
        "REQUEST" or "REQUESTS" => "requests",
        _ => "used"
    };

    private static string? PlanTitle(JsonElement root)
    {
        if (!root.TryGetProperty("subscriptionInfo", out var subEl) || subEl.ValueKind != JsonValueKind.Object) return null;
        return GetString(subEl, "subscriptionTitle");
    }

    private static DateTimeOffset? EpochSeconds(JsonElement element)
    {
        var seconds = ProviderParse.Number(element);
        return seconds is { } s && s > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)s) : null;
    }

    private static JsonElement? GetOrNull(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : null;

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
