using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Pricing;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Cursor;

public sealed class CursorMappedUsage
{
    public string? Plan { get; set; }
    public List<MetricLine> Lines { get; set; } = new();
}

/// <summary>The handful of facts read off Cursor's untyped usage payload. Direct port of CursorPlanUsageFacts.</summary>
public sealed class CursorPlanUsageFacts
{
    public bool IsEnabled { get; }
    public bool HasPlanUsage { get; }
    public double? Limit { get; }
    public double? TotalPercentUsed { get; }
    public string? SpendLimitType { get; }
    public double PooledLimit { get; }

    public CursorPlanUsageFacts(JsonElement usage)
    {
        IsEnabled = !(usage.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False);
        var hasPlanUsage = usage.TryGetProperty("planUsage", out var planUsage) && planUsage.ValueKind == JsonValueKind.Object;
        HasPlanUsage = hasPlanUsage;
        Limit = hasPlanUsage ? ProviderParse.Number(GetOrNull(planUsage, "limit")) : null;
        TotalPercentUsed = hasPlanUsage ? ProviderParse.Number(GetOrNull(planUsage, "totalPercentUsed")) : null;
        var hasSpend = usage.TryGetProperty("spendLimitUsage", out var spendLimitUsage) && spendLimitUsage.ValueKind == JsonValueKind.Object;
        SpendLimitType = hasSpend && spendLimitUsage.TryGetProperty("limitType", out var lt) && lt.ValueKind == JsonValueKind.String
            ? lt.GetString()?.ToLowerInvariant() : null;
        PooledLimit = hasSpend ? ProviderParse.Number(GetOrNull(spendLimitUsage, "pooledLimit")) ?? 0 : 0;
    }

    public bool HasLimit => Limit is not null;
    public bool HasTotalUsagePercent => TotalPercentUsed is not null;
    public bool PlanUsageLimitMissing => HasPlanUsage && !HasLimit;
    public bool PlanUsageUnusable => !HasPlanUsage || PlanUsageLimitMissing;
    public bool IsTeamByShape => SpendLimitType == "team" || PooledLimit > 0;
    public bool ShouldTryGenericRequestFallback => IsEnabled && HasPlanUsage && !HasLimit && !HasTotalUsagePercent;

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}

public enum CursorUsageErrorKind
{
    ConnectionFailed,
    InvalidResponse,
    RequestFailed,
    UsageAfterRefreshFailed,
    RequestBasedUnavailable,
    TotalUsageLimitMissing,
    NoActiveSubscription
}

public sealed class CursorUsageError : Exception, Models.ICategorizedError
{
    public CursorUsageErrorKind Kind { get; }
    public int? StatusCode { get; }
    private readonly string? _message;

    public CursorUsageError(CursorUsageErrorKind kind, int? statusCode = null, string? message = null)
        : base(Describe(kind, statusCode, message))
    {
        Kind = kind;
        StatusCode = statusCode;
        _message = message;
    }

    private static string Describe(CursorUsageErrorKind kind, int? statusCode, string? message) => kind switch
    {
        CursorUsageErrorKind.ConnectionFailed => Providers.ProviderUsageErrorText.ConnectionFailed,
        CursorUsageErrorKind.InvalidResponse => Providers.ProviderUsageErrorText.InvalidResponse,
        CursorUsageErrorKind.RequestFailed => Providers.ProviderUsageErrorText.RequestFailed(statusCode ?? 0),
        CursorUsageErrorKind.UsageAfterRefreshFailed => "Usage request failed after refresh. Try again.",
        CursorUsageErrorKind.RequestBasedUnavailable => message ?? "Cursor usage data unavailable.",
        CursorUsageErrorKind.TotalUsageLimitMissing => "Total usage limit missing from API response.",
        CursorUsageErrorKind.NoActiveSubscription => "No active Cursor subscription.",
        _ => "Cursor usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        CursorUsageErrorKind.ConnectionFailed => Models.ErrorCategory.Network,
        CursorUsageErrorKind.InvalidResponse or CursorUsageErrorKind.TotalUsageLimitMissing => Models.ErrorCategory.Decoding,
        CursorUsageErrorKind.RequestFailed => Models.ErrorCategoryExtensions.FromHttpStatus(StatusCode ?? 0),
        CursorUsageErrorKind.RequestBasedUnavailable or CursorUsageErrorKind.NoActiveSubscription => Models.ErrorCategory.NotAvailable,
        CursorUsageErrorKind.UsageAfterRefreshFailed => Models.ErrorCategory.Other,
        _ => Models.ErrorCategory.Other
    };
}

public static class CursorUsageMapper
{
    public const long BillingPeriodMs = MetricPeriod.MonthMs;

    public static CursorMappedUsage MapUsage(JsonElement usage, string? planName, JsonElement? creditGrants, double stripeBalanceCents)
    {
        var facts = new CursorPlanUsageFacts(usage);
        if (!facts.IsEnabled || !usage.TryGetProperty("planUsage", out var planUsage) || planUsage.ValueKind != JsonValueKind.Object)
        {
            throw new CursorUsageError(CursorUsageErrorKind.NoActiveSubscription);
        }

        var normalizedPlan = planName?.Trim().ToLowerInvariant() ?? "";
        if (!facts.HasLimit && !facts.HasTotalUsagePercent) throw new CursorUsageError(CursorUsageErrorKind.TotalUsageLimitMissing);

        var lines = new List<MetricLine>();
        AppendCredits(creditGrants, stripeBalanceCents, lines);

        var planUsedCents = ProviderParse.Number(GetOrNull(planUsage, "totalSpend"))
            ?? ((facts.Limit ?? 0) - (ProviderParse.Number(GetOrNull(planUsage, "remaining")) ?? 0));
        double computedPercentUsed = 0;
        if (facts.Limit is { } limitVal && limitVal > 0) computedPercentUsed = planUsedCents / limitVal * 100;
        var totalUsagePercent = facts.TotalPercentUsed ?? computedPercentUsed;

        var cycle = BillingCycle(usage);
        var hasSpendLimitUsage = usage.TryGetProperty("spendLimitUsage", out var spendLimitUsage) && spendLimitUsage.ValueKind == JsonValueKind.Object;
        var isTeamAccount = normalizedPlan == "team" || facts.IsTeamByShape;

        if (isTeamAccount)
        {
            if (facts.Limit is not { } limitCents)
            {
                throw new CursorUsageError(CursorUsageErrorKind.RequestBasedUnavailable, message: "Cursor request-based usage data unavailable. Try again later.");
            }
            lines.Add(new MetricLine.Progress("Total usage", ProviderParse.CentsToDollars(planUsedCents), ProviderParse.CentsToDollars(limitCents), ProgressFormat.DollarsValue, cycle.ResetsAt, cycle.PeriodDurationMs));
        }
        else
        {
            lines.Add(new MetricLine.Progress("Total usage", totalUsagePercent, 100, ProgressFormat.PercentValue, cycle.ResetsAt, cycle.PeriodDurationMs));
        }

        if (ProviderParse.Number(GetOrNull(planUsage, "autoPercentUsed")) is { } autoPercentUsed)
        {
            lines.Add(new MetricLine.Progress("Auto usage", autoPercentUsed, 100, ProgressFormat.PercentValue, cycle.ResetsAt, cycle.PeriodDurationMs));
        }
        if (ProviderParse.Number(GetOrNull(planUsage, "apiPercentUsed")) is { } apiPercentUsed)
        {
            lines.Add(new MetricLine.Progress("API usage", apiPercentUsed, 100, ProgressFormat.PercentValue, cycle.ResetsAt, cycle.PeriodDurationMs));
        }

        if (hasSpendLimitUsage)
        {
            var limit = ProviderParse.Number(GetOrNull(spendLimitUsage, "individualLimit")) ?? ProviderParse.Number(GetOrNull(spendLimitUsage, "pooledLimit")) ?? 0;
            var remaining = ProviderParse.Number(GetOrNull(spendLimitUsage, "individualRemaining")) ?? ProviderParse.Number(GetOrNull(spendLimitUsage, "pooledRemaining")) ?? 0;
            var spent = OnDemandSpendCents(spendLimitUsage, limit, remaining);
            if (limit > 0)
            {
                lines.Add(new MetricLine.Progress("On-demand", ProviderParse.CentsToDollars(spent), ProviderParse.CentsToDollars(limit), ProgressFormat.DollarsValue));
            }
            else if (spent > 0)
            {
                lines.Add(new MetricLine.Values("On-demand", new List<MetricValue> { new(ProviderParse.CentsToDollars(spent), MetricKind.Dollars) }));
            }
        }

        return new CursorMappedUsage { Plan = PlanLabel(planName), Lines = lines };
    }

    private static double OnDemandSpendCents(JsonElement spendLimitUsage, double limit, double remaining)
    {
        var reported = new[] { "individualUsed", "pooledUsed", "totalSpend" }
            .Select(k => ProviderParse.Number(GetOrNull(spendLimitUsage, k)))
            .Where(v => v is not null).Select(v => v!.Value).ToList();
        var positive = reported.FirstOrDefault(v => v > 0, double.NaN);
        if (!double.IsNaN(positive)) return positive;
        var inferred = Math.Max(0, limit - remaining);
        return inferred > 0 ? inferred : (reported.Count > 0 ? reported[0] : 0);
    }

    public static CursorMappedUsage MapRequestBasedUsage(JsonElement? usage, string? planName, string unavailableMessage)
    {
        var lines = new List<MetricLine>();
        if (usage is { } u && u.TryGetProperty("gpt-4", out var gpt4) && gpt4.ValueKind == JsonValueKind.Object
            && ProviderParse.Number(GetOrNull(gpt4, "maxRequestUsage")) is { } limit && limit > 0)
        {
            var used = ProviderParse.Number(GetOrNull(gpt4, "numRequests")) ?? 0;
            DateTimeOffset? cycleStart = u.TryGetProperty("startOfMonth", out var som) && som.ValueKind == JsonValueKind.String
                ? AIUsageISO8601.Parse(som.GetString()!) : null;
            lines.Add(new MetricLine.Progress("Requests", used, limit, ProgressFormat.CountValue("requests"),
                cycleStart?.AddMilliseconds(BillingPeriodMs), BillingPeriodMs));
        }

        if (lines.Count == 0) throw new CursorUsageError(CursorUsageErrorKind.RequestBasedUnavailable, message: unavailableMessage);
        return new CursorMappedUsage { Plan = PlanLabel(planName), Lines = lines };
    }

    public static (bool ShouldFallback, string Message) ShouldUseRequestBasedFallback(JsonElement usage, string? planName, bool planInfoUnavailable)
    {
        var facts = new CursorPlanUsageFacts(usage);
        if (!facts.IsEnabled) return (false, "");

        var normalizedPlan = planName?.Trim().ToLowerInvariant() ?? "";

        if (facts.PlanUsageUnusable && normalizedPlan == "enterprise") return (true, "Enterprise usage data unavailable. Try again later.");
        if (facts.PlanUsageUnusable && normalizedPlan == "team") return (true, "Team request-based usage data unavailable. Try again later.");
        if (facts.PlanUsageUnusable && !facts.HasTotalUsagePercent && normalizedPlan.Length == 0 && planInfoUnavailable)
        {
            return (true, "Cursor request-based usage data unavailable. Try again later.");
        }
        if (facts.IsTeamByShape && facts.PlanUsageLimitMissing) return (true, "Cursor request-based usage data unavailable. Try again later.");

        return (false, "");
    }

    public static ProviderUsageHistory AppendSpendLines(List<CursorUsageCsvRow> rows, DateTimeOffset now, ModelPricing pricing, List<MetricLine> lines)
    {
        var costByDay = new Dictionary<string, double>();
        var tokensByDay = new Dictionary<string, int>();
        var modelsByDay = new Dictionary<string, Dictionary<string, ModelAccumulator>>();
        var unknownModelsByDay = new Dictionary<string, HashSet<string>>();

        foreach (var row in rows)
        {
            var day = Providers.DailyUsageAccumulator.DayKey(row.Date);
            var model = row.Model.Trim();
            if (row.ImputedCostDollars is not { } cost)
            {
                if (row.Tokens.TotalTokens > 0 && model.Length > 0)
                {
                    if (!unknownModelsByDay.TryGetValue(day, out var set)) { set = new HashSet<string>(); unknownModelsByDay[day] = set; }
                    set.Add(model);
                }
                continue;
            }
            costByDay[day] = costByDay.GetValueOrDefault(day) + cost;
            tokensByDay[day] = tokensByDay.GetValueOrDefault(day) + row.Tokens.TotalTokens;
            var modelName = model.Length == 0 ? ModelUsageEntry.UnattributedModelName : model;
            var family = model.Length == 0 ? modelName : FamilyName(model, pricing);
            if (!modelsByDay.TryGetValue(day, out var models)) { models = new Dictionary<string, ModelAccumulator>(); modelsByDay[day] = models; }
            if (!models.TryGetValue(family, out var acc)) { acc = new ModelAccumulator(); models[family] = acc; }
            acc.Add(modelName, row.Tokens.TotalTokens, cost);
        }

        var daily = tokensByDay.Keys.OrderByDescending(k => k, StringComparer.Ordinal)
            .Select(day => new DailyUsageEntry(day, tokensByDay.GetValueOrDefault(day), Math.Round((costByDay.GetValueOrDefault(day)) * 100, MidpointRounding.AwayFromZero) / 100))
            .ToList();
        var series = new DailyUsageSeries(daily);
        var modelUsage = new ModelUsageSeries(
            modelsByDay.Keys.OrderByDescending(k => k, StringComparer.Ordinal)
                .Select(day => new DailyModelUsageEntry(day, modelsByDay[day].Select(kv => kv.Value.Entry(kv.Key)).ToList()))
                .ToList());

        Providers.SpendTileMapper.AppendTokenUsage(series, lines, now, estimated: true, unknownModelsByDay: unknownModelsByDay, modelUsage: modelUsage, modelSourceNote: "From your Cursor usage export");
        Providers.SpendTileMapper.AppendUsageTrend(series, lines, now, "From your Cursor usage export");
        return new ProviderUsageHistory(series, modelUsage, unknownModelsByDay);
    }

    private static string FamilyName(string model, ModelPricing pricing)
    {
        var canonical = pricing.Supplement.CanonicalName(model) ?? model;
        if (!canonical.EndsWith("-fast", StringComparison.Ordinal)) return canonical;
        var basev = canonical[..^"-fast".Length];
        return basev.Length == 0 ? canonical : basev;
    }

    private sealed class ModelAccumulator
    {
        public int Tokens;
        public double? CostUSD;
        private readonly Dictionary<string, (int Tokens, double? CostUSD)> _variants = new();

        public void Add(string variant, int tokens, double? costUSD)
        {
            Tokens += tokens;
            if (costUSD is { } c) CostUSD = (CostUSD ?? 0) + c;
            var existing = _variants.GetValueOrDefault(variant, (0, null));
            double? combined = costUSD.HasValue ? (existing.CostUSD ?? 0) + costUSD.Value : existing.CostUSD;
            _variants[variant] = (existing.Tokens + tokens, combined);
        }

        public ModelUsageEntry Entry(string model)
        {
            var list = _variants.Select(kv => new ModelUsageVariant(kv.Key, kv.Value.Tokens, kv.Value.CostUSD)).ToList();
            var isTrivial = list.Count == 1 && list[0].Model == model;
            return new ModelUsageEntry(model, Tokens, CostUSD, isTrivial ? null : list);
        }
    }

    public static double StripeBalanceCents(JsonElement? body)
    {
        if (body is not { } b || ProviderParse.Number(GetOrNull(b, "customerBalance")) is not { } balance || balance >= 0) return 0;
        return Math.Abs(balance);
    }

    private static void AppendCredits(JsonElement? creditGrants, double stripeBalanceCents, List<MetricLine> lines)
    {
        var hasCreditGrants = creditGrants is { } cg && cg.TryGetProperty("hasCreditGrants", out var hcg) && hcg.ValueKind == JsonValueKind.True;
        var grantTotalCents = hasCreditGrants ? ProviderParse.Number(GetOrNull(creditGrants!.Value, "totalCents")) ?? 0 : 0;
        var grantUsedCents = hasCreditGrants ? ProviderParse.Number(GetOrNull(creditGrants!.Value, "usedCents")) ?? 0 : 0;
        var hasValidGrantData = hasCreditGrants && grantTotalCents > 0;
        var combinedTotalCents = (hasValidGrantData ? grantTotalCents : 0) + stripeBalanceCents;
        var remainingCents = Math.Max(0, combinedTotalCents - (hasValidGrantData ? grantUsedCents : 0));

        if (combinedTotalCents <= 0) return;
        lines.Add(new MetricLine.Values("Credits", new List<MetricValue> { new(ProviderParse.CentsToDollars(remainingCents), MetricKind.Dollars) }));
    }

    private static (DateTimeOffset? ResetsAt, long PeriodDurationMs) BillingCycle(JsonElement usage)
    {
        var cycleStart = ProviderParse.Number(GetOrNull(usage, "billingCycleStart"));
        var cycleEnd = ProviderParse.Number(GetOrNull(usage, "billingCycleEnd"));
        if (cycleStart is null || cycleEnd is null || cycleEnd <= cycleStart)
        {
            return (cycleEnd is { } ce ? DateTimeOffset.FromUnixTimeMilliseconds((long)ce) : null, BillingPeriodMs);
        }
        return (DateTimeOffset.FromUnixTimeMilliseconds((long)cycleEnd.Value), (long)(cycleEnd.Value - cycleStart.Value));
    }

    public static string? PlanLabel(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.TitleCased(char.IsWhiteSpace);
    }

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
