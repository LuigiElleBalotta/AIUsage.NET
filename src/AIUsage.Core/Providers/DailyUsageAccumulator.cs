using AIUsage.Core.Models;

namespace AIUsage.Core.Providers;

/// <summary>
/// Accumulates priced per-day usage (tokens, cost, per-model breakdown) then assembles a LogUsageScan.
/// Direct port of the Swift DailyUsageAccumulator.
/// </summary>
public sealed class DailyUsageAccumulator
{
    private readonly Dictionary<string, int> _tokensByDay = new();
    private readonly Dictionary<string, double> _costByDay = new();
    private readonly Dictionary<string, HashSet<string>> _unknownModelsByDay = new();
    private readonly Dictionary<string, Dictionary<string, ModelAccumulator>> _modelsByDay = new();

    public static string DayKey(DateTimeOffset date) => date.ToString("yyyy-MM-dd");
    public static string DayKey(DateTime date) => date.ToString("yyyy-MM-dd");

    public void Add(string day, int tokens, double cost, string model)
    {
        _tokensByDay[day] = _tokensByDay.GetValueOrDefault(day) + tokens;
        _costByDay[day] = _costByDay.GetValueOrDefault(day) + cost;
        if (!_modelsByDay.TryGetValue(day, out var models))
        {
            models = new Dictionary<string, ModelAccumulator>();
            _modelsByDay[day] = models;
        }
        if (!models.TryGetValue(model, out var acc))
        {
            acc = new ModelAccumulator();
            models[model] = acc;
        }
        acc.Add(tokens, cost);
    }

    public void AddUnknownModel(string day, string model)
    {
        if (!_unknownModelsByDay.TryGetValue(day, out var set))
        {
            set = new HashSet<string>();
            _unknownModelsByDay[day] = set;
        }
        set.Add(model);
    }

    public LogUsageScan Build()
    {
        var days = _tokensByDay.Keys.OrderByDescending(k => k, StringComparer.Ordinal)
            .Select(day => new DailyUsageEntry(day, _tokensByDay.GetValueOrDefault(day), _costByDay.GetValueOrDefault(day)))
            .ToList();

        var modelUsage = new ModelUsageSeries(
            _modelsByDay.Keys.OrderByDescending(k => k, StringComparer.Ordinal)
                .Select(day => new DailyModelUsageEntry(
                    day,
                    _modelsByDay[day].Select(kv => kv.Value.Entry(kv.Key)).ToList()))
                .ToList());

        return new LogUsageScan(new DailyUsageSeries(days), modelUsage, _unknownModelsByDay);
    }

    /// <summary>Merge already-built scans (native + pi slice) by replaying per-model daily usage through a fresh accumulator.</summary>
    public static LogUsageScan? Merged(IEnumerable<LogUsageScan?> scans)
    {
        var present = scans.Where(s => s is not null).Select(s => s!).ToList();
        if (present.Count == 0) return null;

        var accumulator = new DailyUsageAccumulator();
        foreach (var scan in present)
        {
            foreach (var day in scan.ModelUsage?.Daily ?? Array.Empty<DailyModelUsageEntry>())
            {
                foreach (var model in day.Models)
                {
                    if (model.CostUSD is not { } cost) continue;
                    accumulator.Add(day.Date, model.TotalTokens, cost, model.Model);
                }
            }
            foreach (var (day, models) in scan.UnknownModelsByDay)
            {
                foreach (var model in models)
                {
                    accumulator.AddUnknownModel(day, model);
                }
            }
        }
        return accumulator.Build();
    }

    private sealed class ModelAccumulator
    {
        private int _tokens;
        private double? _costUSD;

        public void Add(int tokens, double? costUSD)
        {
            _tokens += tokens;
            if (costUSD is { } c) _costUSD = (_costUSD ?? 0) + c;
        }

        public ModelUsageEntry Entry(string model) => new(model, _tokens, _costUSD);
    }
}
