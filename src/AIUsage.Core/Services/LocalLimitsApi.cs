using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Stores;
using AIUsage.Core.Support;

namespace AIUsage.Core.Services;

/// <summary>
/// Machine-facing limits serializer shared by the one-shot CLI and the local HTTP API. Provider
/// refresh/mapping remains the single source of truth; this edge only selects scalar resources
/// explicitly declared on <see cref="WidgetDescriptor"/> (via ExportingLimit) and gives them stable
/// public names. Direct port of the Swift LocalLimitsAPI.
/// </summary>
public static class LocalLimitsApi
{
    public const string Schema = "openusage.limits.v1";

    private static readonly JsonSerializerOptions EncodeOptions = new() { WriteIndented = false };

    public static byte[] Encode(IReadOnlyList<string> providerIds, LocalUsageApi.State state)
    {
        var providers = new Dictionary<string, object?>();
        foreach (var providerId in providerIds)
        {
            if (!state.Snapshots.TryGetValue(providerId, out var snapshot)) continue;
            var descriptors = state.LimitDescriptorsOrEmpty.GetValueOrDefault(providerId) ?? new List<WidgetDescriptor>();
            providers[providerId] = WireProvider(snapshot, descriptors, state.GeneratedAtOrNow);
        }

        var errors = providerIds
            .Where(id => state.ErrorsOrEmpty.ContainsKey(id))
            .Select(id => new Dictionary<string, object?> { ["providerId"] = id, ["message"] = state.ErrorsOrEmpty[id] })
            .ToList();

        var envelope = new Dictionary<string, object?>
        {
            ["schema"] = Schema,
            ["generatedAt"] = AIUsageISO8601.ToStringIso(state.GeneratedAtOrNow),
            ["providers"] = providers,
            ["errors"] = errors
        };

        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(envelope, EncodeOptions);
        }
        catch
        {
            return System.Text.Encoding.UTF8.GetBytes("{\"errors\":[],\"providers\":{},\"schema\":\"openusage.limits.v1\"}");
        }
    }

    private static Dictionary<string, object?> WireProvider(ProviderSnapshot snapshot, List<WidgetDescriptor> descriptors, DateTimeOffset generatedAt)
    {
        var fetchedAt = snapshot.RefreshedAt;
        var expiresAt = fetchedAt + RefreshSetting.Interval;
        var stale = generatedAt >= expiresAt;

        var resources = new Dictionary<string, object?>();
        foreach (var descriptor in descriptors)
        {
            var line = snapshot.Line(descriptor.MetricLabel);
            if (line is null) continue;
            foreach (var resource in descriptor.LimitResources ?? Array.Empty<LimitResourceDescriptor>())
            {
                var wire = WireResource(resource, line);
                if (wire is not null) resources[resource.Key] = wire;
            }
        }

        return new Dictionary<string, object?>
        {
            ["displayName"] = snapshot.DisplayName,
            ["plan"] = snapshot.Plan,
            ["fetchedAt"] = AIUsageISO8601.ToStringIso(fetchedAt),
            ["expiresAt"] = AIUsageISO8601.ToStringIso(expiresAt),
            ["stale"] = stale,
            ["resources"] = resources
        };
    }

    private static Dictionary<string, object?>? WireResource(LimitResourceDescriptor resource, MetricLine line)
    {
        var unit = ProgressUnit(line) ?? resource.Unit;

        switch (resource.Source, line)
        {
            case (LimitResourceDescriptor.ResourceSource.Progress, MetricLine.Progress p):
            case (LimitResourceDescriptor.ResourceSource.ProgressOrValue, MetricLine.Progress p2):
            {
                var progress = line as MetricLine.Progress;
                return ProgressResource(resource, unit, progress!);
            }

            case (LimitResourceDescriptor.ResourceSource.Value valueSource, MetricLine.Values values):
            case (LimitResourceDescriptor.ResourceSource.ProgressOrValue progressOrValueSource, MetricLine.Values values2):
            {
                var v = (MetricLine.Values)line;
                MetricKind expectedKind;
                string? expectedLabel;
                if (resource.Source is LimitResourceDescriptor.ResourceSource.Value vs)
                {
                    expectedKind = vs.Kind;
                    expectedLabel = vs.Label;
                }
                else if (resource.Source is LimitResourceDescriptor.ResourceSource.ProgressOrValue pv)
                {
                    expectedKind = pv.Kind;
                    expectedLabel = pv.Label;
                }
                else
                {
                    return null;
                }

                var metric = v.ValuesList.FirstOrDefault(val => val.Kind == expectedKind && (expectedLabel is null || val.Label == expectedLabel));
                if (metric is null) return null;

                var result = new Dictionary<string, object?>
                {
                    ["kind"] = resource.Kind == LimitResourceDescriptor.ResourceKind.Balance ? "balance" : "consumption",
                    ["unit"] = unit
                };
                if (resource.Kind == LimitResourceDescriptor.ResourceKind.Balance)
                {
                    result["available"] = metric.Number;
                }
                else
                {
                    result["used"] = metric.Number;
                }
                if (v.Expiries.Count > 0)
                {
                    result["expiresAt"] = v.Expiries.OrderBy(e => e).Select(AIUsageISO8601.ToStringIso).ToList();
                }
                if (resource.Estimated || metric.Estimated)
                {
                    result["estimated"] = true;
                }
                return result;
            }

            default:
                return null;
        }
    }

    private static Dictionary<string, object?> ProgressResource(LimitResourceDescriptor resource, string unit, MetricLine.Progress p)
    {
        var boundedLimit = Math.Max(0, p.Limit);
        var boundedUsed = Math.Max(0, p.Used);

        var result = new Dictionary<string, object?>
        {
            ["kind"] = resource.Kind == LimitResourceDescriptor.ResourceKind.Balance ? "balance" : "consumption",
            ["unit"] = unit,
            ["limit"] = boundedLimit,
            ["remaining"] = Math.Max(0, boundedLimit - boundedUsed),
            ["utilization"] = boundedLimit > 0 ? boundedUsed / boundedLimit : null
        };
        if (resource.Kind == LimitResourceDescriptor.ResourceKind.Consumption)
        {
            result["used"] = boundedUsed;
        }
        else
        {
            result["available"] = boundedUsed;
        }
        if (p.ResetsAt is { } reset) result["resetsAt"] = AIUsageISO8601.ToStringIso(reset);
        if (p.PeriodDurationMs is { } periodMs) result["windowSeconds"] = periodMs / 1000.0;
        if (resource.Estimated) result["estimated"] = true;
        return result;
    }

    /// <summary>A descriptor names the stable resource and supplies a fallback unit for value rows.
    /// Progress rows carry their actual runtime unit, which can vary by plan (e.g. Cursor Total Usage
    /// is percent on individual plans and requests on request-based Enterprise plans).</summary>
    private static string? ProgressUnit(MetricLine line)
    {
        if (line is not MetricLine.Progress p) return null;
        return p.Format switch
        {
            ProgressFormat.Percent => "percent",
            ProgressFormat.Dollars => "usd",
            ProgressFormat.Count c => c.Suffix.Trim().NilIfEmpty(),
            _ => null
        };
    }
}
