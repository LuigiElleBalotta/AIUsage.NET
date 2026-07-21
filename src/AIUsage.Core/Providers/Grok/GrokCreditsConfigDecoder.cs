using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Grok;

public sealed record GrokCreditsConfig(string PeriodType, double UsedPercent, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, double OnDemandCap)
{
    public long PeriodDurationMs => (long)Math.Round((PeriodEnd - PeriodStart).TotalMilliseconds);
}

/// <summary>Decodes Grok's proto-JSON credits config. Direct port of GrokCreditsConfigDecoder.</summary>
public static class GrokCreditsConfigDecoder
{
    public const string WeeklyPeriodType = "USAGE_PERIOD_TYPE_WEEKLY";

    public static GrokCreditsConfig Decode(byte[] responseBody)
    {
        var body = ProviderParse.JsonObject(responseBody);
        if (body is not { } root || !root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            throw new GrokUsageError(GrokUsageErrorKind.InvalidResponse);
        }
        if (!config.TryGetProperty("currentPeriod", out var period) || period.ValueKind != JsonValueKind.Object)
        {
            throw new GrokUsageError(GrokUsageErrorKind.InvalidResponse);
        }
        var periodType = GetString(period, "type")?.Trim();
        if (string.IsNullOrEmpty(periodType)) throw new GrokUsageError(GrokUsageErrorKind.InvalidResponse);

        var start = DateOf(GetOrNull(period, "start"));
        var end = DateOf(GetOrNull(period, "end"));
        if (start is null || end is null || end <= start) throw new GrokUsageError(GrokUsageErrorKind.InvalidResponse);

        double percent = 0;
        if (config.TryGetProperty("creditUsagePercent", out var percentEl))
        {
            if (ProviderParse.Number(percentEl) is not { } number || !double.IsFinite(number))
            {
                throw new GrokUsageError(GrokUsageErrorKind.InvalidResponse);
            }
            percent = number;
        }

        double onDemandCap = 0;
        if (config.TryGetProperty("onDemandCap", out var capObj))
        {
            if (capObj.ValueKind != JsonValueKind.Object) throw new GrokUsageError(GrokUsageErrorKind.InvalidResponse);
            var valEl = GetOrNull(capObj, "val");
            var cap = valEl is null ? 0 : ProviderParse.Number(valEl);
            if (cap is not { } c || !double.IsFinite(c)) throw new GrokUsageError(GrokUsageErrorKind.InvalidResponse);
            onDemandCap = c;
        }

        return new GrokCreditsConfig(periodType, percent, start.Value, end.Value, onDemandCap);
    }

    private static DateTimeOffset? DateOf(JsonElement? value)
    {
        if (value is not { } v || v.ValueKind != JsonValueKind.String) return null;
        var raw = v.GetString()?.Trim();
        return string.IsNullOrEmpty(raw) ? null : AIUsageISO8601.Parse(raw);
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
