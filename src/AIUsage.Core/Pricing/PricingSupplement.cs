using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Core.Support;

namespace AIUsage.Core.Pricing;

/// <summary>
/// AIUsage's own pricing feed: models no public catalog carries, fast-variant multipliers, and
/// alias rules mapping provider log/CSV slugs to canonical pricing keys.
/// </summary>
public sealed class PricingSupplement
{
    public Dictionary<string, ModelRates> Pricing { get; }
    public Dictionary<string, double> FastMultipliers { get; }
    public List<(Regex Pattern, string Canonical)> AliasRules { get; }
    public string? UpdatedAt { get; }

    public PricingSupplement(
        Dictionary<string, ModelRates>? pricing = null,
        Dictionary<string, double>? fastMultipliers = null,
        List<(Regex Pattern, string Canonical)>? aliasRules = null,
        string? updatedAt = null)
    {
        Pricing = pricing ?? new Dictionary<string, ModelRates>();
        FastMultipliers = fastMultipliers ?? new Dictionary<string, double>();
        AliasRules = aliasRules ?? new List<(Regex, string)>();
        UpdatedAt = updatedAt;
    }

    public string? CanonicalName(string model)
    {
        foreach (var (pattern, canonical) in AliasRules)
        {
            if (pattern.IsMatch(model)) return canonical;
        }
        return null;
    }

    public double? FastMultiplier(string model)
    {
        if (FastMultipliers.TryGetValue(model, out var exact)) return exact;
        var normalized = PricingCatalog.NormalizedKey(model);
        foreach (var part in normalized.Split('/', ':'))
        {
            foreach (var (baseModel, multiplier) in FastMultipliers)
            {
                if (MatchesModelSuffix(part, PricingCatalog.NormalizedKey(baseModel))) return multiplier;
            }
        }
        return null;
    }

    private static bool MatchesModelSuffix(string part, string baseModel)
    {
        var index = part.LastIndexOf(baseModel, StringComparison.Ordinal);
        if (index < 0) return false;
        var suffix = part[(index + baseModel.Length)..];
        return suffix.Length == 0 || suffix.StartsWith('-');
    }

    public static PricingSupplement Decode(byte[] data)
    {
        var file = JsonSerializer.Deserialize<SupplementFile>(data, JsonDefaults.Options)
                   ?? throw new InvalidOperationException("Invalid pricing supplement JSON");

        var pricing = new Dictionary<string, ModelRates>();
        foreach (var (model, entry) in file.Pricing)
        {
            pricing[model] = new ModelRates
            {
                InputPerMillion = entry.InputPerMillion,
                OutputPerMillion = entry.OutputPerMillion,
                CacheWritePerMillion = entry.CacheWritePerMillion ?? entry.InputPerMillion,
                CacheReadPerMillion = entry.CacheReadPerMillion ?? entry.InputPerMillion * 0.1,
                CacheReadIsExplicit = entry.CacheReadPerMillion.HasValue
            };
        }

        var rules = new List<(Regex, string)>();
        foreach (var rule in file.AliasRules)
        {
            try
            {
                rules.Add((new Regex(rule.Pattern, RegexOptions.Compiled), rule.Canonical));
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogTag.Cache, $"pricing supplement: invalid alias pattern '{rule.Pattern}' skipped: {ex.Message}");
            }
        }

        return new PricingSupplement(pricing, file.FastMultipliers ?? new Dictionary<string, double>(), rules, file.UpdatedAt);
    }

    private sealed class SupplementFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("pricing")]
        public Dictionary<string, Entry> Pricing { get; set; } = new();
        [System.Text.Json.Serialization.JsonPropertyName("fast_multipliers")]
        public Dictionary<string, double>? FastMultipliers { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("alias_rules")]
        public List<Rule> AliasRules { get; set; } = new();

        public sealed class Entry
        {
            [System.Text.Json.Serialization.JsonPropertyName("input_per_million")]
            public double InputPerMillion { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("output_per_million")]
            public double OutputPerMillion { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("cache_write_per_million")]
            public double? CacheWritePerMillion { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("cache_read_per_million")]
            public double? CacheReadPerMillion { get; set; }
        }

        public sealed class Rule
        {
            [System.Text.Json.Serialization.JsonPropertyName("pattern")]
            public string Pattern { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("canonical")]
            public string Canonical { get; set; } = "";
        }
    }
}
