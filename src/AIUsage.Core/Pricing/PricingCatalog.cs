namespace AIUsage.Core.Pricing;

/// <summary>
/// One pricing source (LiteLLM or models.dev) as a flat model-key -> rates table with ccusage's
/// lookup semantics: exact key first, then a boundary-aware fuzzy match. Direct port of the Swift
/// PricingCatalog.
/// </summary>
public sealed class PricingCatalog
{
    public Dictionary<string, ModelRates> Entries { get; }
    public string? RetrievedAt { get; }

    public PricingCatalog(Dictionary<string, ModelRates>? entries = null, string? retrievedAt = null)
    {
        Entries = entries ?? new Dictionary<string, ModelRates>();
        RetrievedAt = retrievedAt;
    }

    public (string Key, ModelRates Rates)? FindExact(string model) =>
        Entries.TryGetValue(model, out var rates) ? (model, rates) : null;

    public (string Key, ModelRates Rates)? FindFuzzy(string model)
    {
        var normalizedModel = NormalizedKey(model);
        (string Key, ModelRates Rates)? best = null;
        foreach (var (key, rates) in Entries)
        {
            if (!KeyMatches(key, model, normalizedModel)) continue;
            if (best is { } current)
            {
                if (key.Length > current.Key.Length || (key.Length == current.Key.Length && string.CompareOrdinal(key, current.Key) < 0))
                {
                    best = (key, rates);
                }
            }
            else
            {
                best = (key, rates);
            }
        }
        return best;
    }

    public PricingCatalog Merging(PricingCatalog other)
    {
        var merged = new Dictionary<string, ModelRates>(Entries);
        foreach (var (key, value) in other.Entries) merged[key] = value;
        return new PricingCatalog(merged, other.RetrievedAt ?? RetrievedAt);
    }

    public static string NormalizedKey(string value)
    {
        if (!value.Contains('.') && !value.Contains('@')) return value;
        return value.Replace('.', '-').Replace('@', '-');
    }

    public static bool KeyMatches(string candidate, string model, string normalizedModel)
    {
        if (ContainsKey(model, candidate) || ContainsKey(candidate, model)) return true;
        var normalizedCandidate = NormalizedKey(candidate);
        return ContainsKey(normalizedModel, normalizedCandidate) || ContainsKey(normalizedCandidate, normalizedModel);
    }

    public static bool ContainsKey(string value, string key)
    {
        if (key.Length == 0) return false;
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        if (keyBytes.Length > valueBytes.Length) return false;

        for (var start = 0; start <= valueBytes.Length - keyBytes.Length; start++)
        {
            var matches = true;
            for (var i = 0; i < keyBytes.Length; i++)
            {
                if (valueBytes[start + i] != keyBytes[i]) { matches = false; break; }
            }
            if (!matches) continue;

            var beforeOk = start == 0 || !IsAsciiAlphanumeric(valueBytes[start - 1]);
            if (!beforeOk) continue;

            var suffix = valueBytes[(start + keyBytes.Length)..];
            if (SuffixAllowsMatch(keyBytes, suffix)) return true;
        }
        return false;
    }

    private static bool SuffixAllowsMatch(byte[] key, byte[] suffix)
    {
        if (suffix.Length == 0) return true;
        var separator = suffix[0];
        if (IsAsciiAlphanumeric(separator)) return false;
        return !SuffixStartsWithNumericModelVersion(key, suffix);
    }

    private static bool SuffixStartsWithNumericModelVersion(byte[] key, byte[] suffix)
    {
        const int dateSuffixDigits = 8;
        if (key.Length == 0 || !IsAsciiDigit(key[^1])) return false;
        if (suffix.Length == 0) return false;
        var separator = suffix[0];
        if (separator != (byte)'-' && separator != (byte)'.') return false;

        var rest = suffix[1..];
        var digitCount = 0;
        while (digitCount < rest.Length && IsAsciiDigit(rest[digitCount])) digitCount++;
        if (digitCount == 0) return false;

        var afterDigits = digitCount < rest.Length ? rest[digitCount] : (byte?)null;
        var isDateSuffix = digitCount == dateSuffixDigits && (afterDigits is null || !IsAsciiAlphanumeric(afterDigits.Value));
        return !isDateSuffix;
    }

    private static bool IsAsciiDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';
    private static bool IsAsciiAlphanumeric(byte b) =>
        IsAsciiDigit(b) || (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z');
}
