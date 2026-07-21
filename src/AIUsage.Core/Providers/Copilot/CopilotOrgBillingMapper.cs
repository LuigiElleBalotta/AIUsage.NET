using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Copilot;

/// <summary>Normalizes GitHub org billing responses into org-level Copilot meters. Direct port.</summary>
public static class CopilotOrgBillingMapper
{
    public static List<string> OrgLogins(HttpResponseResult response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<string>();
            var result = new List<string>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String)
                {
                    var value = login.GetString()?.Trim().NilIfEmpty();
                    if (value is not null) result.Add(value);
                }
            }
            return result;
        }
        catch
        {
            return new List<string>();
        }
    }

    public static List<MetricLine>? UsageLines(HttpResponseResult response)
    {
        var body = ProviderParse.JsonObject(response.Body);
        return body is { } b ? UsageLines(b) : null;
    }

    public static List<MetricLine>? UsageLines(JsonElement body)
    {
        if (!body.TryGetProperty("usageItems", out var items) || items.ValueKind != JsonValueKind.Array) return null;

        var creditItems = items.EnumerateArray()
            .Where(item => IsCopilot(GetString(item, "product")) && IsCreditUnit(GetString(item, "unitType")))
            .ToList();
        if (creditItems.Count == 0) return null;

        var credits = creditItems.Sum(i => Math.Max(0, ProviderParse.Number(GetOrNull(i, "grossQuantity")) ?? 0));
        var spend = creditItems.Sum(i => Math.Max(0, ProviderParse.Number(GetOrNull(i, "netAmount")) ?? 0));

        return new List<MetricLine>
        {
            new MetricLine.Values("Org Credits", new List<MetricValue> { new(credits, MetricKind.Count, "credits") }),
            new MetricLine.Values("Org Spend", new List<MetricValue> { new(spend, MetricKind.Dollars) })
        };
    }

    private static bool IsCopilot(string? value) => value?.Trim().ToLowerInvariant() == "copilot";

    private static bool IsCreditUnit(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized == "ai-units" || normalized == "ai-credits";
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
