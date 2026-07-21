using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Services;

/// <summary>
/// Routing + JSON for the read-only local usage API, kept pure (no network) so both the one-shot CLI
/// and the loopback HTTP listener call the exact same logic. Direct port of the Swift LocalUsageAPI.
/// Wire format follows docs/local-http-api.md (camelCase providerId/color/fetchedAt, type-tagged
/// lines, {"error": code} bodies).
/// </summary>
public static class LocalUsageApi
{
    /// <summary>Everything one request needs, captured from the live stores into an immutable value.</summary>
    public sealed record State(
        IReadOnlyList<string> EnabledOrderedIds,
        IReadOnlySet<string> KnownIds,
        IReadOnlyDictionary<string, ProviderSnapshot> Snapshots,
        IReadOnlyDictionary<string, List<WidgetDescriptor>>? LimitDescriptors = null,
        IReadOnlyDictionary<string, string>? Errors = null,
        DateTimeOffset? GeneratedAt = null)
    {
        public IReadOnlyDictionary<string, List<WidgetDescriptor>> LimitDescriptorsOrEmpty =>
            LimitDescriptors ?? new Dictionary<string, List<WidgetDescriptor>>();

        public IReadOnlyDictionary<string, string> ErrorsOrEmpty => Errors ?? new Dictionary<string, string>();

        public DateTimeOffset GeneratedAtOrNow => GeneratedAt ?? DateTimeOffset.UtcNow;

        /// <summary>Every known provider id the token names — an exact id, or (once multi-account
        /// ships) a family id naming every account card of that family. Pure string matching: the
        /// answer never depends on which account is logged in or what's enabled. Sorted for stable
        /// output. Empty means the token names nothing (404).</summary>
        public List<string> MatchingProviderIds(string token) =>
            KnownIds.Where(id => id == token).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    public sealed record Response(int Status, byte[]? Body);

    private static readonly JsonSerializerOptions EncodeOptions = new();

    public static Response Respond(string method, string path, State state)
    {
        if (method == "OPTIONS")
        {
            return new Response(204, null);
        }

        var segments = path.Split('?', 2)[0].Split('/').Where(s => s.Length > 0).ToList();

        // (count, seg0, seg1)
        if (segments.Count == 2 && segments[0] == "v1" && segments[1] == "limits")
        {
            if (method != "GET") return Error(405, "method_not_allowed");
            return new Response(200, LocalLimitsApi.Encode(state.EnabledOrderedIds, state));
        }

        if (segments.Count == 3 && segments[0] == "v1" && segments[1] == "limits")
        {
            if (method != "GET") return Error(405, "method_not_allowed");
            var providerIds = state.MatchingProviderIds(segments[2]);
            if (providerIds.Count == 0) return Error(404, "provider_not_found");
            return new Response(200, LocalLimitsApi.Encode(providerIds, state));
        }

        if (segments.Count == 2 && segments[0] == "v1" && segments[1] == "usage")
        {
            if (method != "GET") return Error(405, "method_not_allowed");
            var snapshots = state.EnabledOrderedIds.Where(state.Snapshots.ContainsKey).Select(id => state.Snapshots[id]).ToList();
            return new Response(200, Encode(snapshots.Select(WireSnapshot).ToList()));
        }

        if (segments.Count == 3 && segments[0] == "v1" && segments[1] == "usage")
        {
            if (method != "GET") return Error(405, "method_not_allowed");
            var providerIds = state.MatchingProviderIds(segments[2]);
            if (providerIds.Count == 0) return Error(404, "provider_not_found");
            var snapshots = providerIds.Where(state.Snapshots.ContainsKey).Select(id => state.Snapshots[id]).ToList();
            return new Response(200, Encode(snapshots.Select(WireSnapshot).ToList()));
        }

        return Error(404, "not_found");
    }

    public static readonly Response Busy = Error(503, "server_busy");

    private static Response Error(int status, string code) =>
        new(status, System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{code}\"}}"));

    private static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, EncodeOptions);

    // MARK: - Wire types (the documented public shape, distinct from the internal cache JSON)

    private static Dictionary<string, object?> WireSnapshot(ProviderSnapshot snapshot) => new()
    {
        ["providerId"] = snapshot.ProviderId,
        ["displayName"] = snapshot.DisplayName,
        ["plan"] = snapshot.Plan,
        ["lines"] = snapshot.Lines.Select(WireLine).ToList(),
        ["fetchedAt"] = AIUsageISO8601.ToStringIso(snapshot.RefreshedAt)
    };

    private static Dictionary<string, object?> WireLine(MetricLine line)
    {
        switch (line)
        {
            case MetricLine.Text t:
                return new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["label"] = t.Label,
                    ["value"] = t.Value,
                    ["color"] = t.ColorHex,
                    ["subtitle"] = t.Subtitle
                };

            case MetricLine.Values v:
                // Serialize as the legacy "text" shape (one combined value string) so existing local
                // API integrations keep working: dollars in full, counts compact — the same string the
                // mapper used to produce (e.g. "$5.17 · 9.2M tokens"). Per-model hover details are
                // UI-only and intentionally omitted from this documented public wire shape.
                return new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["label"] = v.Label,
                    ["value"] = LegacyValueString(v.ValuesList),
                    ["color"] = v.ColorHex,
                    ["subtitle"] = null,
                    ["resetsAt"] = v.Expiries.Count > 0 ? AIUsageISO8601.ToStringIso(v.Expiries.Min()) : null
                };

            case MetricLine.Progress p:
                return new Dictionary<string, object?>
                {
                    ["type"] = "progress",
                    ["label"] = p.Label,
                    ["used"] = p.Used,
                    ["limit"] = p.Limit,
                    ["format"] = WireFormat(p.Format),
                    ["resetsAt"] = p.ResetsAt is { } r ? AIUsageISO8601.ToStringIso(r) : null,
                    ["periodDurationMs"] = p.PeriodDurationMs,
                    ["color"] = p.ColorHex
                };

            case MetricLine.Badge b:
                return new Dictionary<string, object?>
                {
                    ["type"] = "badge",
                    ["label"] = b.Label,
                    ["text"] = b.BadgeText,
                    ["color"] = b.ColorHex,
                    ["subtitle"] = b.Subtitle
                };

            case MetricLine.Chart c:
                return new Dictionary<string, object?>
                {
                    ["type"] = "barChart",
                    ["label"] = c.Label,
                    ["points"] = c.Points.Select(pt => new Dictionary<string, object?>
                    {
                        ["label"] = pt.Label,
                        ["value"] = pt.Value,
                        ["valueLabel"] = pt.ValueLabel
                    }).ToList(),
                    ["note"] = c.Note,
                    ["color"] = null
                };

            default:
                return new Dictionary<string, object?> { ["type"] = "unknown", ["label"] = line.Label };
        }
    }

    private static Dictionary<string, object?> WireFormat(ProgressFormat format) => format switch
    {
        ProgressFormat.Percent => new Dictionary<string, object?> { ["kind"] = "percent" },
        ProgressFormat.Dollars => new Dictionary<string, object?> { ["kind"] = "dollars" },
        ProgressFormat.Count c => new Dictionary<string, object?> { ["kind"] = "count", ["suffix"] = c.Suffix },
        _ => new Dictionary<string, object?> { ["kind"] = "percent" }
    };

    /// <summary>The legacy combined string for a Values row: each value formatted (dollars full so
    /// cents survive, counts compact) and joined with " · ".</summary>
    private static string LegacyValueString(IReadOnlyList<MetricValue> values) =>
        string.Join(" · ", values.Select(v => MetricFormatter.StringFor(v, v.Kind == MetricKind.Count ? MetricFormatter.Style.Tray : MetricFormatter.Style.Full)));
}
