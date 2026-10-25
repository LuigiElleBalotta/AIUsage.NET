using System.Text.Json;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

/// <summary>One normalized assistant row from either local schema generation.</summary>
internal sealed record OpenCodeUsageRow(
    string? Id,
    double Ms,
    double? UpdatedMs,
    double? Cost,
    long Tokens,
    string Model,
    string ProviderId,
    bool IsV2)
{
    /// <summary>Timestamp used to pick the newest copy of a duplicated message ID.</summary>
    public double EffectiveMs => UpdatedMs ?? Ms;
}

/// <summary>
/// SQL and payload parsing for OpenCode's local usage tables. Kept separate from the scanner so the
/// schema-specific details stay readable and the scanner stays focused on orchestration.
/// </summary>
internal static class OpenCodeUsageQuery
{
    private static readonly string ProviderFilter =
        "(" + string.Join(",", OpenCodeUsageScanner.HostedProviderIds.Select(p => $"'{p}'")) + ")";

    private const string TokensProjection = """
        json_array(
          json_type(data,'$.tokens'),
          json_extract(data,'$.tokens.total'),
          json_extract(data,'$.tokens.input'),
          json_extract(data,'$.tokens.output'),
          json_extract(data,'$.tokens.reasoning'),
          json_extract(data,'$.tokens.cache.read'),
          json_extract(data,'$.tokens.cache.write'))
        """;

    public static string LegacySql(long cutoffMs, HashSet<string>? columns)
    {
        var updated = columns is null || columns.Contains("time_updated") ? "time_updated" : "NULL";
        return $"""
            SELECT json_group_array(json_array(
                     id,
                     time_created,
                     {updated},
                     json_extract(data,'$.cost'),
                     {TokensProjection},
                     COALESCE(json_extract(data,'$.modelID'), json_extract(data,'$.model.id'), ''),
                     COALESCE(json_extract(data,'$.providerID'), json_extract(data,'$.model.providerID'))))
            FROM message
            WHERE time_created >= {cutoffMs}
              AND json_valid(data)
              AND json_extract(data,'$.role') = 'assistant'
              AND COALESCE(json_extract(data,'$.providerID'), json_extract(data,'$.model.providerID')) IN {ProviderFilter};
            """;
    }

    public static string V2Sql(long cutoffMs, HashSet<string>? columns)
    {
        var includeType = columns is null || columns.Contains("type");
        var assistantFilter = includeType
            ? "(lower(type) = 'assistant' OR json_extract(data,'$.role') = 'assistant')"
            : "json_extract(data,'$.role') = 'assistant'";
        var updated = columns is null || columns.Contains("time_updated") ? "time_updated" : "NULL";

        return $"""
            SELECT json_group_array(json_array(
                     id,
                     time_created,
                     {updated},
                     json_extract(data,'$.cost'),
                     {TokensProjection},
                     COALESCE(json_extract(data,'$.model.id'), json_extract(data,'$.modelID'), ''),
                     COALESCE(json_extract(data,'$.model.providerID'), json_extract(data,'$.providerID'))))
            FROM session_message
            WHERE time_created >= {cutoffMs}
              AND json_valid(data)
              AND {assistantFilter}
              AND COALESCE(json_extract(data,'$.model.providerID'), json_extract(data,'$.providerID')) IN {ProviderFilter};
            """;
    }

    public static readonly string LegacyProbeSql = $"""
        SELECT 1 FROM message
        WHERE json_valid(data)
          AND json_extract(data,'$.role') = 'assistant'
          AND COALESCE(json_extract(data,'$.providerID'), json_extract(data,'$.model.providerID')) IN {ProviderFilter}
        LIMIT 1;
        """;

    public static readonly string V2ProbeSql = $"""
        SELECT 1 FROM session_message
        WHERE json_valid(data)
          AND (lower(type) = 'assistant' OR json_extract(data,'$.role') = 'assistant')
          AND COALESCE(json_extract(data,'$.model.providerID'), json_extract(data,'$.providerID')) IN {ProviderFilter}
        LIMIT 1;
        """;

    public static readonly string V2ProbeNoTypeSql = $"""
        SELECT 1 FROM session_message
        WHERE json_valid(data)
          AND json_extract(data,'$.role') = 'assistant'
          AND COALESCE(json_extract(data,'$.model.providerID'), json_extract(data,'$.providerID')) IN {ProviderFilter}
        LIMIT 1;
        """;

    /// <summary>
    /// Parses the aggregated query payload. Returns false when the payload (or one of its rows) is
    /// malformed, while still returning any valid rows that could be read. Callers log only this
    /// diagnostic string, never the payload itself, so message contents cannot reach the log.
    /// </summary>
    public static bool TryParseRows(
        string json,
        bool isV2,
        out List<OpenCodeUsageRow> rows,
        out string? error)
    {
        rows = new List<OpenCodeUsageRow>();
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "payload is not a JSON array";
                return false;
            }

            var malformed = false;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array)
                {
                    malformed = true;
                    continue;
                }
                var arr = entry.EnumerateArray().ToList();
                if (arr.Count < 7)
                {
                    malformed = true;
                    continue;
                }

                var id = IdOf(arr[0]);
                if (!TryReadTimestamp(arr[1], out var ms) || ms is null)
                {
                    malformed = true;
                    continue;
                }

                if (!TryReadTimestamp(arr[2], out var updatedMs))
                {
                    malformed = true;
                    continue;
                }

                double? cost = null;
                if (arr[3].ValueKind != JsonValueKind.Null)
                {
                    if (ProviderParse.Number(arr[3]) is not { } c || !double.IsFinite(c) || c < 0)
                    {
                        malformed = true;
                        continue;
                    }
                    cost = c;
                }

                if (!TryReadTokens(arr[4], isV2, out var tokens))
                {
                    malformed = true;
                    continue;
                }
                var model = StringValue(arr[5]);
                var providerId = StringValue(arr[6]);
                if (Array.IndexOf(OpenCodeUsageScanner.HostedProviderIds, providerId) < 0) continue;

                // Keep empty assistant rows: they are valid dedup candidates that must be able to
                // suppress an older copy of the same message ID. They are dropped after selection.
                rows.Add(new OpenCodeUsageRow(id, ms.Value, updatedMs, cost, tokens, model, providerId, isV2));
            }

            if (malformed) error = "one or more rows were malformed";
            return !malformed;
        }
        catch (JsonException)
        {
            error = "payload is not valid JSON";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            return false;
        }
    }

    /// <summary>Reads an optional millisecond timestamp; JSON null (or a missing column) is valid.</summary>
    private static bool TryReadTimestamp(JsonElement element, out double? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (ProviderParse.Number(element) is not { } ms
            || !double.IsFinite(ms)
            || ms < -62_135_596_800_000d
            || ms > 253_402_300_799_999d)
        {
            return false;
        }
        value = ms;
        return true;
    }

    private static string? IdOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()?.Trim().NilIfEmpty(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null
    };

    private static string StringValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";

    private static bool TryReadTokens(JsonElement element, bool isV2, out long tokens)
    {
        tokens = 0;
        if (element.ValueKind != JsonValueKind.Array)
        {
            if (!TryReadOptionalTokenCount(element, out var scalar)) return false;
            tokens = scalar ?? 0;
            return true;
        }

        var parts = element.EnumerateArray().ToList();
        if (parts.Count != 7) return false;

        if (parts[0].ValueKind == JsonValueKind.String)
        {
            var tokensType = parts[0].GetString();
            if (tokensType is not ("object" or "null")) return false;
        }
        else if (parts[0].ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        if (!TryReadOptionalTokenCount(parts[1], out var total)
            || !TryReadOptionalTokenCount(parts[2], out var input)
            || !TryReadOptionalTokenCount(parts[3], out var output)
            || !TryReadOptionalTokenCount(parts[4], out var reasoning)
            || !TryReadOptionalTokenCount(parts[5], out var cacheRead)
            || !TryReadOptionalTokenCount(parts[6], out var cacheWrite))
        {
            return false;
        }

        var hasComponent = input.HasValue || output.HasValue || reasoning.HasValue || cacheRead.HasValue || cacheWrite.HasValue;
        if (!isV2 && total.HasValue)
        {
            tokens = total.Value;
            return true;
        }
        if (isV2 && !hasComponent)
        {
            tokens = total ?? 0;
            return true;
        }

        try
        {
            tokens = checked(
                (input ?? 0)
                + (output ?? 0)
                + (reasoning ?? 0)
                + (cacheRead ?? 0)
                + (cacheWrite ?? 0));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadOptionalTokenCount(JsonElement element, out long? count)
    {
        count = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Number) return false;

        if (element.TryGetInt64(out var exact))
        {
            if (exact < 0) return false;
            count = exact;
            return true;
        }

        if (!element.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value < 0
            || value >= 9_223_372_036_854_775_808d
            || Math.Truncate(value) != value)
        {
            return false;
        }

        count = (long)value;
        return true;
    }
}
