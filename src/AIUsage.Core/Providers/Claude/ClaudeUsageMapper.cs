using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Claude;

public sealed class ClaudeMappedUsage
{
    public string? Plan { get; set; }
    public List<MetricLine> Lines { get; set; } = new();
    public string? Warning { get; set; }
}

public static class ClaudeUsageMapper
{
    public const int SessionPeriodMs = MetricPeriod.SessionMs;
    public const int WeeklyPeriodMs = MetricPeriod.WeekMs;

    public static ClaudeMappedUsage MapUsageResponse(HttpResponseResult response, ClaudeOAuth credentials, DateTimeOffset now)
    {
        ProviderAuthRetry.RequireSuccess(
            response,
            () => new ClaudeAuthError(ClaudeAuthErrorKind.TokenExpired),
            status => new ClaudeUsageError(ClaudeUsageErrorKind.RequestFailed, status));

        var body = ProviderParse.JsonObject(response.Body);
        if (body is not { } root) throw new ClaudeUsageError(ClaudeUsageErrorKind.InvalidResponse);

        var lines = new List<MetricLine>();
        AppendUsageWindow(root, "five_hour", "Session", SessionPeriodMs, lines);
        AppendUsageWindow(root, "seven_day", "Weekly", WeeklyPeriodMs, lines);
        AppendUsageWindow(root, "seven_day_sonnet", "Sonnet", WeeklyPeriodMs, lines);
        AppendScopedWeeklyLimit(root, "Fable", "Fable", lines);
        AppendExtraUsage(root, lines);

        return new ClaudeMappedUsage
        {
            Plan = FormatPlan(credentials.SubscriptionType, credentials.RateLimitTier),
            Lines = lines
        };
    }

    public static ClaudeMappedUsage RateLimitedUsage(ClaudeOAuth credentials, int? retryAfterSeconds)
    {
        var retryText = retryAfterSeconds.HasValue ? FormatRateLimitMinutes(retryAfterSeconds.Value) : null;
        var waitText = retryText is not null ? $"Rate limited, retry in ~{retryText}" : "Rate limited, try again later";
        return new ClaudeMappedUsage
        {
            Plan = FormatPlan(credentials.SubscriptionType, credentials.RateLimitTier),
            Lines = new List<MetricLine>
            {
                new MetricLine.Badge("Status", waitText, "#F59E0B"),
                RateLimitedNote(retryAfterSeconds)
            },
            Warning = RateLimitedWarning(retryAfterSeconds)
        };
    }

    public static string RateLimitedWarning(int? retryAfterSeconds)
    {
        const string basev = "Updates blocked by Anthropic. Be patient — manual refreshes will make it worse.";
        if (retryAfterSeconds is not { } seconds) return basev;
        return $"{basev} Retrying in ~{FormatRateLimitMinutes(seconds)}.";
    }

    public const string MissingProfileScopeWarning =
        "Re-login for live usage. Run `claude` and sign in again to restore session and weekly limits.";

    public static MetricLine RateLimitedNote(int? retryAfterSeconds)
    {
        var retryText = retryAfterSeconds.HasValue ? FormatRateLimitMinutes(retryAfterSeconds.Value) : null;
        var noteText = retryText is not null ? $"Live usage rate limited - retry in ~{retryText}" : "Live usage rate limited - data may be stale";
        return new MetricLine.Text("Note", noteText);
    }

    public static int? ParseRetryAfterSeconds(HttpResponseResult response, DateTimeOffset now)
    {
        var raw = response.Header("retry-after")?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (int.TryParse(raw, out var seconds) && seconds >= 0) return seconds;
        if (DateTimeOffset.TryParse(raw, out var date))
        {
            return Math.Max(0, (int)Math.Ceiling((date - now).TotalSeconds));
        }
        return null;
    }

    public static string? FormatPlan(string? subscriptionType, string? rateLimitTier)
    {
        var raw = subscriptionType?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        var basev = raw.TitleCased(c => c == ' ', lowercasingTail: true);

        if (rateLimitTier is null) return basev;
        var match = System.Text.RegularExpressions.Regex.Match(rateLimitTier, @"\d+x");
        return match.Success ? $"{basev} {match.Value}" : basev;
    }

    private static void AppendUsageWindow(JsonElement root, string key, string label, long periodDurationMs, List<MetricLine> lines)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object) return;
        var used = ProviderParse.Number(GetOrNull(value, "utilization"));
        if (used is null) return;
        lines.Add(new MetricLine.Progress(label, used.Value, 100, ProgressFormat.PercentValue,
            ResetDate(GetOrNull(value, "resets_at")), periodDurationMs));
    }

    private static void AppendScopedWeeklyLimit(JsonElement root, string modelName, string label, List<MetricLine> lines)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("kind", out var kind) || kind.GetString() != "weekly_scoped") continue;
            if (!entry.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) continue;
            if (!scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) continue;
            if (!model.TryGetProperty("display_name", out var name) || name.GetString() != modelName) continue;
            var used = ProviderParse.Number(GetOrNull(entry, "percent"));
            if (used is null) continue;
            lines.Add(new MetricLine.Progress(label, used.Value, 100, ProgressFormat.PercentValue,
                ResetDate(GetOrNull(entry, "resets_at")), WeeklyPeriodMs));
            return;
        }
    }

    private static void AppendExtraUsage(JsonElement root, List<MetricLine> lines)
    {
        if (!root.TryGetProperty("extra_usage", out var extra) || extra.ValueKind != JsonValueKind.Object) return;
        if (!extra.TryGetProperty("is_enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True) return;
        var usedCents = ProviderParse.Number(GetOrNull(extra, "used_credits"));
        if (usedCents is null) return;

        var used = ProviderParse.CentsToDollars(usedCents.Value);
        var limitCents = ProviderParse.Number(GetOrNull(extra, "monthly_limit"));
        if (limitCents is { } lc && lc > 0)
        {
            lines.Add(new MetricLine.Progress("Extra usage spent", used, ProviderParse.CentsToDollars(lc), ProgressFormat.DollarsValue));
        }
        else if (used > 0)
        {
            lines.Add(new MetricLine.Values("Extra usage spent", new List<MetricValue> { new(used, MetricKind.Dollars) }));
        }
    }

    private static DateTimeOffset? ResetDate(JsonElement? value)
    {
        if (value is not { } v) return null;
        if (v.ValueKind == JsonValueKind.String)
        {
            var text = v.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text) && AIUsageISO8601.Parse(text) is { } date) return date;
        }
        var number = ProviderParse.Number(v);
        if (number is not { } n || !double.IsFinite(n)) return null;
        var milliseconds = Math.Abs(n) < 1e10 ? n * 1000 : n;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }

    private static string FormatRateLimitMinutes(int seconds)
    {
        if (seconds <= 0) return "now";
        return $"{(int)Math.Ceiling(seconds / 60.0)}m";
    }

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
