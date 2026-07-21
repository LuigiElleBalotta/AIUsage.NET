using System.Text.RegularExpressions;
using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers;

/// <summary>
/// Turns local daily token/cost data into the shared Today / Yesterday / Last 30 Days spend tiles.
/// Direct port of the Swift SpendTileMapper.
/// </summary>
public static class SpendTileMapper
{
    public static void AppendTokenUsage(
        DailyUsageSeries usage,
        List<MetricLine> lines,
        DateTimeOffset? nowOverride = null,
        bool estimated = true,
        IReadOnlyDictionary<string, HashSet<string>>? unknownModelsByDay = null,
        ModelUsageSeries? modelUsage = null,
        string? modelSourceNote = null)
    {
        unknownModelsByDay ??= new Dictionary<string, HashSet<string>>();
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var today = DailyUsageAccumulator.DayKey(now);
        var yesterday = DailyUsageAccumulator.DayKey(now.AddDays(-1));

        var todayEntry = usage.Daily.FirstOrDefault(d => DayKeyFromUsageDate(d.Date) == today);
        if (todayEntry is not null && HasUsage(todayEntry))
        {
            lines.Add(DayUsageLine("Today", todayEntry, estimated,
                SortedModels(unknownModelsByDay.GetValueOrDefault(today)),
                ModelBreakdown(modelUsage, new HashSet<string> { today }, todayEntry.TotalTokens, todayEntry.CostUSD, modelSourceNote)));
        }

        var yesterdayEntry = usage.Daily.FirstOrDefault(d => DayKeyFromUsageDate(d.Date) == yesterday);
        if (yesterdayEntry is not null && HasUsage(yesterdayEntry))
        {
            lines.Add(DayUsageLine("Yesterday", yesterdayEntry, estimated,
                SortedModels(unknownModelsByDay.GetValueOrDefault(yesterday)),
                ModelBreakdown(modelUsage, new HashSet<string> { yesterday }, yesterdayEntry.TotalTokens, yesterdayEntry.CostUSD, modelSourceNote)));
        }

        var totalTokens = usage.Daily.Sum(d => d.TotalTokens);
        var costSamples = usage.Daily.Where(d => d.CostUSD.HasValue).Select(d => d.CostUSD!.Value).ToList();
        double? totalCost = costSamples.Count == 0 ? null : costSamples.Sum();
        if (totalTokens > 0 || (totalCost ?? 0) > 0)
        {
            var allUnknown = new HashSet<string>();
            foreach (var set in unknownModelsByDay.Values) allUnknown.UnionWith(set);
            var allDays = usage.Daily.Select(d => DayKeyFromUsageDate(d.Date)).Where(k => k is not null).Select(k => k!).ToHashSet();
            lines.Add(new MetricLine.Values(
                "Last 30 Days",
                SpendValues(totalTokens, totalCost, estimated),
                UnknownModels: SortedModels(allUnknown),
                ModelBreakdown: ModelBreakdown(modelUsage, allDays, totalTokens, totalCost, modelSourceNote)));
        }
    }

    private static bool HasUsage(DailyUsageEntry entry) => entry.TotalTokens > 0 || (entry.CostUSD ?? 0) > 0;

    public static void AppendUsageTrend(DailyUsageSeries usage, List<MetricLine> lines, DateTimeOffset? nowOverride, string note)
    {
        var points = TrendPoints(usage, nowOverride ?? DateTimeOffset.UtcNow);
        if (points.Count == 0) return;
        lines.Add(new MetricLine.Chart("Usage Trend", points, note));
    }

    private static List<MetricChartPoint> TrendPoints(DailyUsageSeries usage, DateTimeOffset now)
    {
        var tokensByDay = new Dictionary<string, double>();
        foreach (var day in usage.Daily)
        {
            var tokens = (double)day.TotalTokens;
            if (!double.IsFinite(tokens) || tokens < 0) continue;
            var key = DayKeyFromUsageDate(day.Date);
            if (key is null) continue;
            tokensByDay[key] = tokensByDay.GetValueOrDefault(key) + tokens;
        }
        if (!tokensByDay.Values.Any(v => v > 0)) return new List<MetricChartPoint>();

        var today = now.Date;
        var points = new List<MetricChartPoint>();
        for (var offset = UsageHistoryWindow.PreviousDays; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset);
            var key = DailyUsageAccumulator.DayKey(day);
            var tokens = tokensByDay.GetValueOrDefault(key);
            points.Add(new MetricChartPoint(
                tokens,
                Formatters.MonthDayLabel(day),
                MetricFormatter.Number(tokens, MetricKind.Count, MetricFormatter.Style.Row) + " tokens"));
        }
        return points;
    }

    private static readonly Regex IsoDatePrefix = new(@"^\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);
    private static readonly Regex EightDigits = new(@"^\d{8}$", RegexOptions.Compiled);

    private static string? DayKeyFromUsageDate(string rawDate)
    {
        var value = rawDate.Trim();
        if (value.Length == 0) return null;

        if (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$")) return value;

        if (AIUsageISO8601.Parse(value) is { } date) return DailyUsageAccumulator.DayKey(date.UtcDateTime);

        var match = IsoDatePrefix.Match(value);
        if (match.Success) return match.Value;

        if (EightDigits.IsMatch(value))
        {
            return $"{value[..4]}-{value.Substring(4, 2)}-{value.Substring(6, 2)}";
        }

        if (DateTime.TryParseExact(value, "MMM dd, yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return DailyUsageAccumulator.DayKey(parsed);
        }

        return null;
    }

    private static MetricLine DayUsageLine(string label, DailyUsageEntry entry, bool estimated, IReadOnlyList<string> unknownModels, ModelUsageBreakdown? breakdown)
    {
        return new MetricLine.Values(label, SpendValues(entry.TotalTokens, entry.CostUSD, estimated), UnknownModels: unknownModels, ModelBreakdown: breakdown);
    }

    private static IReadOnlyList<string> SortedModels(HashSet<string>? models)
    {
        return (models ?? new HashSet<string>()).OrderBy(m => m, StringComparer.Ordinal).ToList();
    }

    private static List<MetricValue> SpendValues(int tokens, double? costUSD, bool estimated)
    {
        var values = new List<MetricValue>();
        if (costUSD is { } cost) values.Add(new MetricValue(cost, MetricKind.Dollars, Estimated: estimated));
        values.Add(new MetricValue(tokens, MetricKind.Count, "tokens"));
        return values;
    }

    private const int NamedModelCap = 5;
    private const double MinVisibleShare = 0.05;

    private sealed class ModelAccumulator
    {
        public int Tokens;
        public double? CostUSD;
        private readonly Dictionary<string, int> _spellingWeights = new();
        private readonly Dictionary<string, (int Tokens, double? CostUSD, Dictionary<string, int> Weights)> _variants = new();

        public string? DisplayName => BestSpelling(_spellingWeights);

        private static string? BestSpelling(Dictionary<string, int> weights)
        {
            if (weights.Count == 0) return null;
            return weights
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key == kv.Key.ToLowerInvariant() ? 0 : 1)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .First().Key;
        }

        public void Add(ModelUsageEntry entry, string spelledAs)
        {
            Tokens += entry.TotalTokens;
            if (entry.CostUSD is { } cost) CostUSD = (CostUSD ?? 0) + cost;
            _spellingWeights[spelledAs] = _spellingWeights.GetValueOrDefault(spelledAs) + Math.Max(entry.TotalTokens, 1);

            var variants = entry.Variants ?? new List<ModelUsageVariant> { new(spelledAs, entry.TotalTokens, entry.CostUSD) };
            foreach (var variant in variants)
            {
                MergeVariant(variant.Model, variant.TotalTokens, variant.CostUSD);
            }
        }

        public void Fold(ModelUsageEntry entry)
        {
            Tokens += entry.TotalTokens;
            if (entry.CostUSD is { } cost) CostUSD = (CostUSD ?? 0) + cost;
            MergeVariant(entry.Model, entry.TotalTokens, entry.CostUSD);
        }

        private void MergeVariant(string model, int tokens, double? costUSD)
        {
            var key = model.ToLowerInvariant();
            if (!_variants.TryGetValue(key, out var existing))
            {
                existing = (0, null, new Dictionary<string, int>());
            }
            existing.Tokens += tokens;
            if (costUSD is { } c) existing.CostUSD = (existing.CostUSD ?? 0) + c;
            existing.Weights[model] = existing.Weights.GetValueOrDefault(model) + Math.Max(tokens, 1);
            _variants[key] = existing;
        }

        public ModelUsageEntry Entry(string model)
        {
            var list = _variants.Select(kv =>
            {
                var (key, (tokens, cost, weights)) = (kv.Key, kv.Value);
                var name = BestSpelling(weights) ?? key;
                return new ModelUsageVariant(name, tokens, cost.HasValue ? RoundToCents(cost.Value) : null);
            }).OrderByDescending(v => v.CostUSD ?? 0).ThenByDescending(v => v.TotalTokens).ThenBy(v => v.Model, StringComparer.OrdinalIgnoreCase).ToList();

            var isTrivial = list.Count == 1 && list[0].Model.Equals(model, StringComparison.OrdinalIgnoreCase);
            return new ModelUsageEntry(model, Tokens, CostUSD.HasValue ? RoundToCents(CostUSD.Value) : null, isTrivial ? null : list);
        }
    }

    private static ModelUsageBreakdown? ModelBreakdown(
        ModelUsageSeries? usage,
        HashSet<string> days,
        int totalTokens,
        double? totalCostUSD,
        string? sourceNote)
    {
        if (usage is null || sourceNote is null || days.Count == 0) return null;

        var byModel = new Dictionary<string, ModelAccumulator>();
        foreach (var day in usage.Daily)
        {
            var key = DayKeyFromUsageDate(day.Date);
            if (key is null || !days.Contains(key)) continue;
            foreach (var model in day.Models)
            {
                if (model.TotalTokens <= 0 && (model.CostUSD ?? 0) <= 0) continue;
                var name = NormalizedModelName(model.Model);
                var lowerKey = name.ToLowerInvariant();
                if (!byModel.TryGetValue(lowerKey, out var acc))
                {
                    acc = new ModelAccumulator();
                    byModel[lowerKey] = acc;
                }
                acc.Add(model, name);
            }
        }

        var sorted = byModel.Select(kv => kv.Value.Entry(kv.Value.DisplayName ?? kv.Key))
            .OrderByDescending(e => e.CostUSD ?? 0)
            .ThenByDescending(e => e.TotalTokens)
            .ThenBy(e => e.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var folded = FoldModelList(sorted);
        if (folded.Count == 0) return null;
        return new ModelUsageBreakdown(totalTokens, totalCostUSD, folded, sourceNote);
    }

    private static string NormalizedModelName(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? ModelUsageEntry.UnattributedModelName : trimmed;
    }

    private static List<ModelUsageEntry> FoldModelList(List<ModelUsageEntry> entries)
    {
        var allPriced = entries.All(e => e.CostUSD.HasValue);
        var costTotal = entries.Sum(e => e.CostUSD ?? 0);
        var tokenTotal = entries.Sum(e => e.TotalTokens);

        double Share(ModelUsageEntry entry)
        {
            if (allPriced && costTotal > 0) return (entry.CostUSD ?? 0) / costTotal;
            if (tokenTotal <= 0) return 0;
            return (double)entry.TotalTokens / tokenTotal;
        }

        var visible = new List<ModelUsageEntry>();
        var other = new ModelAccumulator();
        var namedCount = 0;

        foreach (var entry in entries)
        {
            var isUnattributed = entry.Model.Equals(ModelUsageEntry.UnattributedModelName, StringComparison.OrdinalIgnoreCase);
            if (isUnattributed || Share(entry) < MinVisibleShare)
            {
                other.Fold(entry);
            }
            else if (!entry.CostUSD.HasValue)
            {
                visible.Add(entry);
                namedCount++;
            }
            else if (namedCount < NamedModelCap)
            {
                visible.Add(entry);
                namedCount++;
            }
            else
            {
                other.Fold(entry);
            }
        }

        if (other.Tokens > 0 || (other.CostUSD ?? 0) > 0)
        {
            visible.Add(other.Entry(ModelUsageEntry.OtherModelName));
        }
        return visible;
    }

    private static double RoundToCents(double value) => Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;
}
