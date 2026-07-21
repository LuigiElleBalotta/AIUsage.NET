using System.Collections.Concurrent;

namespace AIUsage.Core.Pricing;

/// <summary>
/// An immutable pricing snapshot: the supplement plus the two public catalogs, with the ccusage
/// resolution order. Direct port of the Swift ModelPricing.
/// </summary>
public sealed class ModelPricing
{
    public PricingSupplement Supplement { get; }
    public PricingCatalog Primary { get; }
    public PricingCatalog Secondary { get; }

    private readonly ConcurrentDictionary<string, ModelRates?> _memo = new();

    public ModelPricing(PricingSupplement supplement, PricingCatalog primary, PricingCatalog secondary)
    {
        Supplement = supplement;
        Primary = primary;
        Secondary = secondary;
    }

    public static readonly ModelPricing Empty = new(new PricingSupplement(), new PricingCatalog(), new PricingCatalog());

    public ModelRates? Resolve(string model)
    {
        if (_memo.TryGetValue(model, out var cached)) return cached;
        var resolved = ResolveUncached(model);
        _memo[model] = resolved;
        return resolved;
    }

    public double? EstimatedCostDollars(string model, TokenBreakdown tokens, bool applyLongContextRates = true)
    {
        var rates = Resolve(model);
        return rates is null ? null : ModelRatesExtensions.CostDollars(rates, tokens, applyLongContextRates);
    }

    private ModelRates? ResolveUncached(string model)
    {
        var canonical = Supplement.CanonicalName(model);
        if (canonical is not null && canonical != model)
        {
            return Lookup(canonical) ?? Lookup(model);
        }
        return Lookup(model);
    }

    private ModelRates? Lookup(string name)
    {
        if (Supplement.Pricing.TryGetValue(name, out var entry)) return entry;
        if (Primary.FindExact(name) is { } exact) return exact.Rates;
        if (FastVariant(name) is { } fast) return fast;
        if (name.EndsWith("-fast", StringComparison.Ordinal)) return Secondary.FindExact(name)?.Rates;
        if (Primary.FindFuzzy(name) is { } fuzzy) return fuzzy.Rates;
        if (Secondary.FindExact(name) is { } secondaryExact) return secondaryExact.Rates;
        return null;
    }

    private ModelRates? FastVariant(string name)
    {
        if (!name.EndsWith("-fast", StringComparison.Ordinal)) return null;
        var baseName = name[..^"-fast".Length];
        if (baseName.Length == 0) return null;
        var baseEntry = BaseEntry(baseName);
        if (baseEntry is null) return null;
        var (key, rates) = baseEntry.Value;

        double multiplier;
        if (rates.FastMultiplier != 1)
        {
            multiplier = rates.FastMultiplier;
        }
        else if (Supplement.FastMultiplier(key) is { } supplementMultiplier)
        {
            multiplier = supplementMultiplier;
        }
        else if (Supplement.FastMultiplier(baseName) is { } supplementMultiplier2)
        {
            multiplier = supplementMultiplier2;
        }
        else
        {
            return null;
        }
        return rates.Scaled(multiplier);
    }

    private (string Key, ModelRates Rates)? BaseEntry(string baseName)
    {
        if (Supplement.Pricing.TryGetValue(baseName, out var entry)) return (baseName, entry);
        return Primary.FindExact(baseName) ?? Primary.FindFuzzy(baseName) ?? Secondary.FindExact(baseName);
    }
}
