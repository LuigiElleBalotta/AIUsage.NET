using System.Text.Json;

namespace AIUsage.Core.Pricing;

/// <summary>
/// Parsers for the LiteLLM and models.dev feeds, plus the compact catalog format used for bundled
/// snapshots and on-disk caches. Direct port of the Swift PricingCatalogCodecs.
/// </summary>
public static class PricingCatalogCodecs
{
    public static PricingCatalog CatalogFromLiteLLM(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Pricing feed is not a JSON object.");

        var entries = new Dictionary<string, ModelRates>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var entry = prop.Value;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var input = DoubleValue(entry, "input_cost_per_token");
            var output = DoubleValue(entry, "output_cost_per_token");
            if (input is null || output is null) continue;

            var cacheWrite = DoubleValue(entry, "cache_creation_input_token_cost");
            var cacheRead = DoubleValue(entry, "cache_read_input_token_cost");

            var rates = new ModelRates
            {
                InputPerMillion = input.Value * 1_000_000,
                OutputPerMillion = output.Value * 1_000_000,
                CacheWritePerMillion = (cacheWrite ?? input.Value) * 1_000_000,
                CacheReadPerMillion = (cacheRead ?? input.Value * 0.1) * 1_000_000,
                CacheReadIsExplicit = cacheRead.HasValue,
                InputAbove200kPerMillion = DoubleValue(entry, "input_cost_per_token_above_200k_tokens") is { } ia ? ia * 1_000_000 : null,
                OutputAbove200kPerMillion = DoubleValue(entry, "output_cost_per_token_above_200k_tokens") is { } oa ? oa * 1_000_000 : null,
                CacheWriteAbove200kPerMillion = DoubleValue(entry, "cache_creation_input_token_cost_above_200k_tokens") is { } cwa ? cwa * 1_000_000 : null,
                CacheReadAbove200kPerMillion = DoubleValue(entry, "cache_read_input_token_cost_above_200k_tokens") is { } cra ? cra * 1_000_000 : null
            };
            if (entry.TryGetProperty("provider_specific_entry", out var pse) && pse.ValueKind == JsonValueKind.Object)
            {
                if (DoubleValue(pse, "fast") is { } fast)
                {
                    rates = rates with { FastMultiplier = fast };
                }
            }
            entries[prop.Name] = rates;
        }
        if (entries.Count == 0) throw new InvalidOperationException("Pricing feed contained no usable model entries.");
        return new PricingCatalog(entries);
    }

    public static PricingCatalog CatalogFromModelsDev(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Pricing feed is not a JSON object.");

        var entries = new Dictionary<string, ModelRates>();
        foreach (var providerName in doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!doc.RootElement.TryGetProperty(providerName, out var provider)) continue;
            if (!provider.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object) continue;
            foreach (var modelProp in models.EnumerateObject())
            {
                if (entries.ContainsKey(modelProp.Name)) continue;
                var model = modelProp.Value;
                if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object) continue;
                var input = DoubleValue(cost, "input");
                var output = DoubleValue(cost, "output");
                if (input is null || output is null) continue;
                var cacheRead = DoubleValue(cost, "cache_read");
                entries[modelProp.Name] = new ModelRates
                {
                    InputPerMillion = input.Value,
                    OutputPerMillion = output.Value,
                    CacheWritePerMillion = DoubleValue(cost, "cache_write") ?? input.Value,
                    CacheReadPerMillion = cacheRead ?? input.Value * 0.1,
                    CacheReadIsExplicit = cacheRead.HasValue
                };
            }
        }
        if (entries.Count == 0) throw new InvalidOperationException("Pricing feed contained no usable model entries.");
        return new PricingCatalog(entries);
    }

    private static double? DoubleValue(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d : null;
    }

    // MARK: - Compact format

    public static PricingCatalog CatalogFromCompact(byte[] data)
    {
        var file = JsonSerializer.Deserialize<CompactCatalog>(data, Support.JsonDefaults.Options)
                   ?? throw new InvalidOperationException("Invalid compact catalog JSON");
        var entries = new Dictionary<string, ModelRates>(file.Models.Count);
        foreach (var (key, model) in file.Models)
        {
            entries[key] = new ModelRates
            {
                InputPerMillion = model.I,
                OutputPerMillion = model.O,
                CacheWritePerMillion = model.Cw,
                CacheReadPerMillion = model.Cr,
                InputAbove200kPerMillion = model.Ia,
                OutputAbove200kPerMillion = model.Oa,
                CacheWriteAbove200kPerMillion = model.Cwa,
                CacheReadAbove200kPerMillion = model.Cra,
                CacheReadIsExplicit = model.Cre ?? true,
                FastMultiplier = model.Fast ?? 1
            };
        }
        return new PricingCatalog(entries, file.RetrievedAt);
    }

    public static byte[] CompactData(PricingCatalog catalog)
    {
        var models = new Dictionary<string, CompactCatalog.Model>(catalog.Entries.Count);
        foreach (var (key, rates) in catalog.Entries)
        {
            models[key] = new CompactCatalog.Model
            {
                I = rates.InputPerMillion,
                O = rates.OutputPerMillion,
                Cw = rates.CacheWritePerMillion,
                Cr = rates.CacheReadPerMillion,
                Ia = rates.InputAbove200kPerMillion,
                Oa = rates.OutputAbove200kPerMillion,
                Cwa = rates.CacheWriteAbove200kPerMillion,
                Cra = rates.CacheReadAbove200kPerMillion,
                Cre = rates.CacheReadIsExplicit ? null : false,
                Fast = rates.FastMultiplier == 1 ? null : rates.FastMultiplier
            };
        }
        var file = new CompactCatalog { RetrievedAt = catalog.RetrievedAt, Models = models };
        return JsonSerializer.SerializeToUtf8Bytes(file, new JsonSerializerOptions { WriteIndented = false });
    }

    private sealed class CompactCatalog
    {
        [System.Text.Json.Serialization.JsonPropertyName("retrieved_at")]
        public string? RetrievedAt { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("models")]
        public Dictionary<string, Model> Models { get; set; } = new();

        public sealed class Model
        {
            public double I { get; set; }
            public double O { get; set; }
            public double Cw { get; set; }
            public double Cr { get; set; }
            public double? Ia { get; set; }
            public double? Oa { get; set; }
            public double? Cwa { get; set; }
            public double? Cra { get; set; }
            public bool? Cre { get; set; }
            public double? Fast { get; set; }
        }
    }
}
