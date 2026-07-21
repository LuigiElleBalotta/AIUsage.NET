using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Core.Models;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Codex;

public sealed class CodexMappedUsage
{
    public string? Plan { get; set; }
    public List<MetricLine> Lines { get; set; } = new();
}

public static class CodexUsageMapper
{
    public const int SessionPeriodMs = MetricPeriod.SessionMs;
    public const int WeeklyPeriodMs = MetricPeriod.WeekMs;
    public const double CreditUsdRate = 0.04;

    public static CodexMappedUsage MapUsageResponse(HttpResponseResult response, HttpResponseResult? resetCredits, DateTimeOffset now)
    {
        ProviderAuthRetry.RequireSuccess(
            response,
            () => new CodexAuthError(CodexAuthErrorKind.TokenExpired),
            status => new CodexUsageError(CodexUsageErrorKind.RequestFailed, status));

        var body = ProviderParse.JsonObject(response.Body);
        if (body is not { } root) throw new CodexUsageError(CodexUsageErrorKind.InvalidResponse);

        var lines = new List<MetricLine>();
        JsonElement? rateLimit = root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object ? rl : null;
        lines.AddRange(ClassifiedWindowLines(
            rateLimit, ("Session", "Weekly"),
            (ProviderParse.Number(response.Header("x-codex-primary-used-percent")), ProviderParse.Number(response.Header("x-codex-secondary-used-percent"))),
            now));

        lines.AddRange(SparkLines(root, now));

        if (ReadResetCredits(root, resetCredits) is { } resets)
        {
            lines.Add(new MetricLine.Values("Rate Limit Resets",
                new List<MetricValue> { new(resets.Count, MetricKind.Count, "available") },
                ExpiriesAt: resets.Expiries));
        }

        if (ReadCreditsRemaining(response, root) is { } remaining)
        {
            lines.Add(new MetricLine.Values("Credits", CreditValues(remaining)));
        }

        return new CodexMappedUsage { Plan = FormatCodexPlan(root), Lines = lines };
    }

    private static MetricLine Progress(string label, double used, JsonElement? resetWindow, DateTimeOffset now, long periodDurationMs) =>
        new MetricLine.Progress(label, used, 100, ProgressFormat.PercentValue, ResetDate(resetWindow, now), periodDurationMs);

    private static MetricLine? WindowLine(string label, double? usedPercent, JsonElement? window, long defaultPeriodMs, DateTimeOffset now)
    {
        if (usedPercent is not { } used) return null;
        var periodMs = ReadPeriodMs(window) ?? defaultPeriodMs;
        return Progress(label, used, window, now, periodMs);
    }

    private enum WindowKind { Session, Weekly }

    private sealed record WindowCandidate(JsonElement Window, double? UsedPercent, WindowKind FallbackKind);

    private static List<MetricLine> ClassifiedWindowLines(JsonElement? rateLimit, (string Session, string Weekly) labels, (double? Primary, double? Secondary) headerPercents, DateTimeOffset now)
    {
        var candidates = new List<WindowCandidate>();
        var primary = WindowCandidateFrom(rateLimit, "primary_window", headerPercents.Primary, WindowKind.Session);
        if (primary is not null) candidates.Add(primary);
        var secondary = WindowCandidateFrom(rateLimit, "secondary_window", headerPercents.Secondary, WindowKind.Weekly);
        if (secondary is not null) candidates.Add(secondary);

        var result = new List<MetricLine>();
        var sessionLine = ClassifiedWindowLine(WindowKind.Session, labels.Session, candidates, now);
        if (sessionLine is not null) result.Add(sessionLine);
        var weeklyLine = ClassifiedWindowLine(WindowKind.Weekly, labels.Weekly, candidates, now);
        if (weeklyLine is not null) result.Add(weeklyLine);
        return result;
    }

    private static WindowCandidate? WindowCandidateFrom(JsonElement? rateLimit, string key, double? headerPercent, WindowKind fallbackKind)
    {
        JsonElement? window = null;
        if (rateLimit is { } rl && rl.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object)
        {
            window = w;
        }
        else if (headerPercent is not null)
        {
            window = default(JsonElement);
        }
        if (window is null) return null;
        var usedPercent = window.Value.ValueKind == JsonValueKind.Object
            ? (ProviderParse.Number(GetOrNull(window.Value, "used_percent")) ?? headerPercent)
            : headerPercent;
        return new WindowCandidate(window.Value, usedPercent, fallbackKind);
    }

    private static MetricLine? ClassifiedWindowLine(WindowKind kind, string label, List<WindowCandidate> candidates, DateTimeOffset now)
    {
        var exact = candidates.FirstOrDefault(c => ExactKind(c.Window) == kind);
        var candidate = exact ?? candidates.FirstOrDefault(c => ExactKind(c.Window) is null && c.FallbackKind == kind);
        if (candidate is null) return null;
        var defaultPeriodMs = kind == WindowKind.Session ? SessionPeriodMs : WeeklyPeriodMs;
        return WindowLine(label, candidate.UsedPercent, candidate.Window.ValueKind == JsonValueKind.Object ? candidate.Window : null, defaultPeriodMs, now);
    }

    private static WindowKind? ExactKind(JsonElement window)
    {
        var periodMs = ReadPeriodMs(window.ValueKind == JsonValueKind.Object ? window : null);
        if (periodMs is null) return null;
        if (periodMs == SessionPeriodMs) return WindowKind.Session;
        if (periodMs == WeeklyPeriodMs) return WindowKind.Weekly;
        return null;
    }

    private static List<MetricLine> SparkLines(JsonElement body, DateTimeOffset now)
    {
        if (!body.TryGetProperty("additional_rate_limits", out var raw) || raw.ValueKind != JsonValueKind.Array) return new List<MetricLine>();
        foreach (var entry in raw.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!IsSparkEntry(entry)) continue;
            if (!entry.TryGetProperty("rate_limit", out var rateLimit) || rateLimit.ValueKind != JsonValueKind.Object) continue;
            return ClassifiedWindowLines(rateLimit, ("Spark", "Spark Weekly"), (null, null), now);
        }
        return new List<MetricLine>();
    }

    private static bool IsSparkEntry(JsonElement entry)
    {
        foreach (var key in new[] { "limit_name", "metered_feature" })
        {
            if (entry.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
                (v.GetString() ?? "").ToLowerInvariant().Contains("spark"))
            {
                return true;
            }
        }
        return false;
    }

    private static DateTimeOffset? ResetDate(JsonElement? window, DateTimeOffset now)
    {
        if (window is not { } w || w.ValueKind != JsonValueKind.Object) return null;
        if (ProviderParse.Number(GetOrNull(w, "reset_at")) is { } resetAt)
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)resetAt);
        }
        if (ProviderParse.Number(GetOrNull(w, "reset_after_seconds")) is { } resetAfter)
        {
            return now.AddSeconds(resetAfter);
        }
        return null;
    }

    private static long? ReadPeriodMs(JsonElement? window)
    {
        if (window is not { } w || w.ValueKind != JsonValueKind.Object) return null;
        var seconds = ProviderParse.Number(GetOrNull(w, "limit_window_seconds"));
        return seconds is { } s ? (long)(s * 1000) : null;
    }

    public static List<MetricValue> CreditValues(double remaining)
    {
        var credits = Math.Max(0, (int)Math.Floor(remaining));
        var usd = credits * CreditUsdRate;
        return new List<MetricValue>
        {
            new(usd, MetricKind.Dollars),
            new(credits, MetricKind.Count, "credits")
        };
    }

    public static (int Count, List<DateTimeOffset> Expiries)? ReadResetCredits(JsonElement body, HttpResponseResult? resetCredits)
    {
        var source = ResetCreditsSource(body, resetCredits);
        if (source is not { } s) return null;
        var count = ProviderParse.Number(GetOrNull(s, "available_count"));
        if (count is not { } c || c < 0) return null;
        return ((int)Math.Floor(c), AvailableExpiries(GetOrNull(s, "credits")));
    }

    private static JsonElement? ResetCreditsSource(JsonElement body, HttpResponseResult? resetCredits)
    {
        if (resetCredits is { } rc && rc.StatusCode is >= 200 and < 300)
        {
            var dedicated = ProviderParse.JsonObject(rc.Body);
            if (dedicated is { } d && ProviderParse.Number(GetOrNull(d, "available_count")) is not null)
            {
                return d;
            }
        }
        return GetOrNull(body, "rate_limit_reset_credits");
    }

    private static List<DateTimeOffset> AvailableExpiries(JsonElement? value)
    {
        if (value is not { } v || v.ValueKind != JsonValueKind.Array) return new List<DateTimeOffset>();
        var expiries = new List<DateTimeOffset>();
        foreach (var credit in v.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object) continue;
            if (credit.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() != "available")
            {
                continue;
            }
            var expiry = ParseExpiry(GetOrNull(credit, "expires_at"));
            if (expiry is { } e) expiries.Add(e);
        }
        expiries.Sort();
        return expiries;
    }

    private static DateTimeOffset? ParseExpiry(JsonElement? value)
    {
        if (value is not { } v) return null;
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s && AIUsageISO8601.Parse(s) is { } date) return date;
        var seconds = ProviderParse.Number(v);
        return seconds is { } sec ? DateTimeOffset.FromUnixTimeSeconds((long)sec) : null;
    }

    private static double? ReadCreditsRemaining(HttpResponseResult response, JsonElement body)
    {
        if (GetOrNull(body, "credits") is { } credits)
        {
            if (ProviderParse.Number(GetOrNull(credits, "balance")) is { } balance) return balance;
            if (credits.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.False) return 0;
        }
        return ProviderParse.Number(response.Header("x-codex-credits-balance"));
    }

    public static string? FormatCodexPlan(JsonElement body)
    {
        if (!body.TryGetProperty("plan_type", out var v) || v.ValueKind != JsonValueKind.String) return null;
        var raw = v.GetString()?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.ToLowerInvariant() switch
        {
            "prolite" => "Pro 5x",
            "pro" => "Pro 20x",
            _ => raw.TitleCased(c => c == '_')
        };
    }

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
