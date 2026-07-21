using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Antigravity;

public sealed record AntigravityModelConfig(string Label, string? ModelId, double RemainingFraction, DateTimeOffset? ResetTime);

/// <summary>Turns Antigravity's quota responses into metric lines. Direct port of AntigravityUsageMapper.</summary>
public static class AntigravityUsageMapper
{
    public static readonly HashSet<string> ModelBlacklist = new()
    {
        "MODEL_CHAT_20706", "MODEL_CHAT_23310",
        "MODEL_GOOGLE_GEMINI_2_5_FLASH", "MODEL_GOOGLE_GEMINI_2_5_FLASH_THINKING",
        "MODEL_GOOGLE_GEMINI_2_5_FLASH_LITE", "MODEL_GOOGLE_GEMINI_2_5_PRO",
        "MODEL_PLACEHOLDER_M19", "MODEL_PLACEHOLDER_M9", "MODEL_PLACEHOLDER_M12"
    };

    public static readonly (string BucketId, string Label, long PeriodMs)[] SummaryBuckets =
    {
        ("gemini-5h", AntigravityMetric.SessionLabel, MetricPeriod.SessionMs),
        ("gemini-weekly", AntigravityMetric.WeeklyLabel, MetricPeriod.WeekMs),
        ("3p-5h", AntigravityMetric.ClaudeLabel, MetricPeriod.SessionMs),
        ("3p-weekly", AntigravityMetric.ClaudeWeeklyLabel, MetricPeriod.WeekMs)
    };

    public static List<MetricLine>? ParseQuotaSummary(byte[] data)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(data).RootElement; } catch { return null; }

        JsonElement? groups = null;
        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("groups", out var g1) && g1.ValueKind == JsonValueKind.Array)
        {
            groups = g1;
        }
        else if (root.TryGetProperty("groups", out var g2) && g2.ValueKind == JsonValueKind.Array)
        {
            groups = g2;
        }
        if (groups is not { } groupsArray)
        {
            AppLog.Warn(LogTag.Plugin("antigravity"), "quota summary response has no decodable groups; treating as not-a-summary");
            return null;
        }

        var pooled = new Dictionary<string, (double Fraction, DateTimeOffset? ResetTime)>();
        foreach (var group in groupsArray.EnumerateArray())
        {
            if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var bucket in buckets.EnumerateArray())
            {
                var id = bucket.TryGetProperty("bucketId", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                if (id is null || !SummaryBuckets.Any(b => b.BucketId == id))
                {
                    AppLog.Warn(LogTag.Plugin("antigravity"), $"quota summary: skipping unrecognized bucket id '{id ?? "<absent>"}'");
                    continue;
                }
                if (pooled.ContainsKey(id)) continue;
                var fraction = bucket.TryGetProperty("remainingFraction", out var fr) && fr.ValueKind == JsonValueKind.Number ? fr.GetDouble() : (double?)null;
                if (fraction is null || !double.IsFinite(fraction.Value))
                {
                    AppLog.Warn(LogTag.Plugin("antigravity"), $"quota summary: bucket '{id}' has no usable remainingFraction; dropping its line");
                    continue;
                }
                var resetTimeRaw = bucket.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
                pooled[id] = (fraction.Value, resetTimeRaw is not null ? AIUsageISO8601.Parse(resetTimeRaw) : null);
            }
        }

        var lines = new List<MetricLine>();
        foreach (var spec in SummaryBuckets)
        {
            if (!pooled.TryGetValue(spec.BucketId, out var entry)) continue;
            lines.Add(Line(spec.Label, entry.Fraction, entry.ResetTime, spec.PeriodMs));
        }
        return lines;
    }

    public static (string? Plan, List<AntigravityModelConfig> Configs)? ParseUserStatus(byte[] data)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(data).RootElement; } catch { return null; }
        if (!root.TryGetProperty("userStatus", out var status) || status.ValueKind != JsonValueKind.Object) return null;

        string? tierName = GetString(GetOrNull(status, "userTier"), "name");
        string? planName = tierName ?? GetString(GetOrNull(GetOrNull(status, "planStatus"), "planInfo"), "planName");
        var plan = FormatPlan(planName);

        var configs = new List<AntigravityModelConfig>();
        if (GetOrNull(status, "cascadeModelConfigData") is { } cascade && GetOrNull(cascade, "clientModelConfigs") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var model in list.EnumerateArray())
            {
                if (ConfigFromLs(model) is { } cfg) configs.Add(cfg);
            }
        }
        return (plan, configs);
    }

    public static List<AntigravityModelConfig>? ParseCommandModelConfigs(byte[] data)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(data).RootElement; } catch { return null; }
        if (!root.TryGetProperty("clientModelConfigs", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var configs = new List<AntigravityModelConfig>();
        foreach (var model in list.EnumerateArray())
        {
            if (ConfigFromLs(model) is { } cfg) configs.Add(cfg);
        }
        return configs;
    }

    public static List<AntigravityModelConfig> ParseCloudCodeModels(byte[] data)
    {
        var result = new List<AntigravityModelConfig>();
        JsonElement root;
        try { root = JsonDocument.Parse(data).RootElement; } catch { return result; }
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object) return result;

        foreach (var prop in models.EnumerateObject())
        {
            var model = prop.Value;
            if (model.TryGetProperty("isInternal", out var internalEl) && internalEl.ValueKind == JsonValueKind.True) continue;
            var label = GetString(model, "displayName")?.NilIfEmpty() ?? GetString(model, "label")?.NilIfEmpty();
            if (label is null) continue;
            var modelId = GetString(model, "model")?.NilIfEmpty() ?? prop.Name;
            var quota = GetOrNull(model, "quotaInfo");
            result.Add(ConfigOf(label, modelId, quota));
        }
        return result;
    }

    public static List<AntigravityModelConfig> ParseQuotaBuckets(byte[] data)
    {
        var result = new List<AntigravityModelConfig>();
        JsonElement root;
        try { root = JsonDocument.Parse(data).RootElement; } catch { return result; }
        if (!root.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) return result;

        foreach (var bucket in buckets.EnumerateArray())
        {
            var id = GetString(bucket, "modelId")?.NilIfEmpty();
            if (id is null) continue;
            var fraction = bucket.TryGetProperty("remainingFraction", out var fr) && fr.ValueKind == JsonValueKind.Number ? fr.GetDouble() : 0;
            var resetRaw = GetString(bucket, "resetTime");
            result.Add(new AntigravityModelConfig(id, id, fraction, resetRaw is not null ? AIUsageISO8601.Parse(resetRaw) : null));
        }
        return result;
    }

    public static string? ParseLoadCodeAssistPlan(byte[] data)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(data).RootElement; } catch { return null; }
        var paidTier = GetString(GetOrNull(root, "paidTier"), "name");
        var currentTier = GetString(GetOrNull(root, "currentTier"), "name");
        return FormatPlan(paidTier ?? currentTier);
    }

    public static string? ParseProject(byte[] data)
    {
        try
        {
            var root = JsonDocument.Parse(data).RootElement;
            return GetString(root, "cloudaicompanionProject")?.NilIfEmpty();
        }
        catch
        {
            return null;
        }
    }

    public static List<MetricLine> BuildLines(List<AntigravityModelConfig> configs)
    {
        var pooled = new Dictionary<string, (double Fraction, DateTimeOffset? ResetTime)>();
        foreach (var config in configs)
        {
            var label = config.Label.Trim();
            if (label.Length == 0) continue;
            if (config.ModelId is not null && ModelBlacklist.Contains(config.ModelId)) continue;

            var pool = PoolLabel(NormalizeLabel(label));
            if (pooled.TryGetValue(pool, out var existing))
            {
                if (config.RemainingFraction < existing.Fraction) pooled[pool] = (config.RemainingFraction, config.ResetTime);
            }
            else
            {
                pooled[pool] = (config.RemainingFraction, config.ResetTime);
            }
        }

        return pooled.OrderBy(kv => SortKey(kv.Key), StringComparer.Ordinal)
            .Select(kv => Line(kv.Key, kv.Value.Fraction, kv.Value.ResetTime, MetricPeriod.SessionMs))
            .ToList();
    }

    public static MetricLine Line(string pool, double fraction, DateTimeOffset? resetTime, long periodMs)
    {
        var clamped = Math.Max(0, Math.Min(1, fraction));
        var used = (1 - clamped) * 100;
        return new MetricLine.Progress(pool, Math.Round(used), 100, ProgressFormat.PercentValue, resetTime, periodMs);
    }

    private static readonly Regex TrailingParenthetical = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    public static string NormalizeLabel(string label)
    {
        var match = TrailingParenthetical.Match(label);
        return match.Success ? label[..match.Index].Trim() : label.Trim();
    }

    public static string PoolLabel(string normalizedLabel) =>
        normalizedLabel.ToLowerInvariant().Contains("gemini") ? AntigravityMetric.SessionLabel : AntigravityMetric.ClaudeLabel;

    public static string SortKey(string poolLabel) => poolLabel == AntigravityMetric.SessionLabel ? $"0_{poolLabel}" : $"1_{poolLabel}";

    public static string? FormatPlan(string? raw)
    {
        var trimmed = raw?.Trim().NilIfEmpty();
        if (trimmed is null) return null;
        const string prefix = "Google AI ";
        if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
        {
            return trimmed[prefix.Length..].TitleCased(char.IsWhiteSpace);
        }
        foreach (var keyword in new[] { "Ultra", "Pro", "Free" })
        {
            if (trimmed.ToLowerInvariant().Contains(keyword.ToLowerInvariant())) return keyword;
        }
        return trimmed.TitleCased(char.IsWhiteSpace);
    }

    private static AntigravityModelConfig? ConfigFromLs(JsonElement model)
    {
        var label = GetString(model, "label");
        var modelId = GetOrNull(model, "modelOrAlias") is { } moa ? GetString(moa, "model") : null;
        var quota = GetOrNull(model, "quotaInfo");
        if (label is null) return null;
        return ConfigOf(label, modelId, quota);
    }

    private static AntigravityModelConfig ConfigOf(string? label, string? modelId, JsonElement? quota)
    {
        var trimmedLabel = label?.Trim().NilIfEmpty() ?? "";
        var fraction = quota is { } q && q.TryGetProperty("remainingFraction", out var fr) && fr.ValueKind == JsonValueKind.Number ? fr.GetDouble() : 0;
        var resetRaw = quota is { } q2 ? GetString(q2, "resetTime") : null;
        return new AntigravityModelConfig(trimmedLabel, modelId, fraction, resetRaw is not null ? AIUsageISO8601.Parse(resetRaw) : null);
    }

    private static string? GetString(JsonElement? obj, string key) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;

    private static JsonElement? GetOrNull(JsonElement? obj, string key) =>
        obj is { } o ? GetOrNull(o, key) : null;
}
