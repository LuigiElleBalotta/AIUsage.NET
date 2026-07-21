using AIUsage.Core.Providers;

namespace AIUsage.Core.Models;

public sealed record DailyUsageEntry(string Date, int TotalTokens, double? CostUSD = null);

public sealed record DailyUsageSeries(IReadOnlyList<DailyUsageEntry> Daily);

public static class UsageHistoryWindow
{
    public const int PreviousDays = 30;

    public static HashSet<string> DayKeys(DateTimeOffset now)
    {
        var today = now.Date;
        var set = new HashSet<string>();
        for (var offset = 0; offset <= PreviousDays; offset++)
        {
            set.Add(DailyUsageAccumulator.DayKey(today.AddDays(-offset)));
        }
        return set;
    }
}

public sealed record ModelUsageVariant(string Model, int TotalTokens, double? CostUSD = null);

public sealed record ModelUsageEntry(string Model, int TotalTokens, double? CostUSD = null, IReadOnlyList<ModelUsageVariant>? Variants = null)
{
    public const string UnattributedModelName = "Unattributed";
    public const string OtherModelName = "Other";
}

public sealed record DailyModelUsageEntry(string Date, IReadOnlyList<ModelUsageEntry> Models);

public sealed record ModelUsageSeries(IReadOnlyList<DailyModelUsageEntry> Daily);

public sealed record ProviderUsageHistory(
    DailyUsageSeries Series,
    ModelUsageSeries? ModelUsage = null,
    IReadOnlyDictionary<string, HashSet<string>>? UnknownModelsByDay = null
);

public sealed record ModelUsageBreakdown(
    int TotalTokens,
    double? TotalCostUSD,
    IReadOnlyList<ModelUsageEntry> Models,
    string SourceNote
);

/// <summary>Result shape shared by the native log scanners (Claude, Codex, Grok).</summary>
public sealed record LogUsageScan(
    DailyUsageSeries Series,
    ModelUsageSeries? ModelUsage,
    IReadOnlyDictionary<string, HashSet<string>> UnknownModelsByDay
);
