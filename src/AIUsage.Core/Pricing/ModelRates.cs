namespace AIUsage.Core.Pricing;

/// <summary>Per-million-token USD rates for one model, plus optional long-context tiers and a fast multiplier.</summary>
public sealed record ModelRates
{
    public double InputPerMillion { get; init; }
    public double OutputPerMillion { get; init; }
    public double CacheWritePerMillion { get; init; }
    public double CacheReadPerMillion { get; init; }

    public double? InputAbove200kPerMillion { get; init; }
    public double? OutputAbove200kPerMillion { get; init; }
    public double? CacheWriteAbove200kPerMillion { get; init; }
    public double? CacheReadAbove200kPerMillion { get; init; }

    public bool CacheReadIsExplicit { get; init; } = true;
    public int LongContextThresholdTokens { get; init; } = 200_000;
    public double FastMultiplier { get; init; } = 1;

    public ModelRates Scaled(double factor) => new()
    {
        InputPerMillion = InputPerMillion * factor,
        OutputPerMillion = OutputPerMillion * factor,
        CacheWritePerMillion = CacheWritePerMillion * factor,
        CacheReadPerMillion = CacheReadPerMillion * factor,
        InputAbove200kPerMillion = InputAbove200kPerMillion * factor,
        OutputAbove200kPerMillion = OutputAbove200kPerMillion * factor,
        CacheWriteAbove200kPerMillion = CacheWriteAbove200kPerMillion * factor,
        CacheReadAbove200kPerMillion = CacheReadAbove200kPerMillion * factor,
        CacheReadIsExplicit = CacheReadIsExplicit,
        LongContextThresholdTokens = LongContextThresholdTokens,
        FastMultiplier = 1
    };
}

/// <summary>Token counts split into the buckets that price differently.</summary>
public sealed class TokenBreakdown
{
    public int Input { get; set; }
    public int CacheWrite5m { get; set; }
    public int CacheWrite1h { get; set; }
    public int CacheRead { get; set; }
    public int Output { get; set; }
    public bool IsFast { get; set; }

    public int PromptTokens => Input + CacheWrite5m + CacheWrite1h + CacheRead;
    public int TotalTokens => Input + CacheWrite5m + CacheWrite1h + CacheRead + Output;
}

public static class ModelRatesExtensions
{
    private const double CacheWrite1hInputMultiplier = 2.0;

    public static double CostDollars(this ModelRates rates, TokenBreakdown tokens, bool applyLongContextRates = true)
    {
        var multiplier = tokens.IsFast ? rates.FastMultiplier : 1;
        var useLongContext = applyLongContextRates && tokens.PromptTokens > rates.LongContextThresholdTokens;

        double SelectedRate(double basev, double? longContext) => useLongContext ? (longContext ?? basev) : basev;

        var inputRate = SelectedRate(rates.InputPerMillion, rates.InputAbove200kPerMillion);
        var outputRate = SelectedRate(rates.OutputPerMillion, rates.OutputAbove200kPerMillion);
        var cacheWriteRate = SelectedRate(rates.CacheWritePerMillion, rates.CacheWriteAbove200kPerMillion);
        var cacheReadRate = SelectedRate(rates.CacheReadPerMillion, rates.CacheReadAbove200kPerMillion);
        var cacheWrite1hRate = SelectedRate(rates.InputPerMillion, rates.InputAbove200kPerMillion) * CacheWrite1hInputMultiplier;

        double Cost(int t, double rate) => t * rate / 1_000_000;

        var cost = Cost(tokens.Input, inputRate)
                   + Cost(tokens.Output, outputRate)
                   + Cost(tokens.CacheWrite5m, cacheWriteRate)
                   + Cost(tokens.CacheWrite1h, cacheWrite1hRate)
                   + Cost(tokens.CacheRead, cacheReadRate);
        return cost * multiplier;
    }
}
