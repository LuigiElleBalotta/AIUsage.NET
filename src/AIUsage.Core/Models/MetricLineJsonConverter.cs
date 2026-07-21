using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIUsage.Core.Models;

/// <summary>
/// JSON (de)serialization for the MetricLine union type, mirroring the wire shape of the Swift
/// edition's Codable implementation (a "type" discriminator plus per-case fields), so a future local
/// HTTP API / CLI port can read the exact same JSON shape. Used both for the on-disk snapshot cache
/// and (eventually) the local API responses.
/// </summary>
public sealed class MetricLineJsonConverter : JsonConverter<MetricLine>
{
    public override MetricLine? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        var label = root.GetProperty("label").GetString() ?? "";

        switch (type)
        {
            case "text":
                return new MetricLine.Text(
                    label,
                    root.GetProperty("value").GetString() ?? "",
                    GetStringOrNull(root, "colorHex"),
                    GetStringOrNull(root, "subtitle"));

            case "values":
                var values = root.GetProperty("values").EnumerateArray()
                    .Select(v => JsonSerializer.Deserialize<MetricValue>(v.GetRawText(), options)!)
                    .ToList();
                var expiries = root.TryGetProperty("expiriesAt", out var expEl)
                    ? expEl.EnumerateArray().Select(e => e.GetDateTimeOffset()).ToList()
                    : null;
                var unknown = root.TryGetProperty("unknownModels", out var unkEl)
                    ? unkEl.EnumerateArray().Select(e => e.GetString()!).ToList()
                    : null;
                var breakdown = root.TryGetProperty("modelBreakdown", out var bEl) && bEl.ValueKind != JsonValueKind.Null
                    ? JsonSerializer.Deserialize<ModelUsageBreakdown>(bEl.GetRawText(), options)
                    : null;
                return new MetricLine.Values(label, values, GetStringOrNull(root, "colorHex"), expiries, unknown, breakdown);

            case "progress":
                var format = JsonSerializer.Deserialize<ProgressFormat>(root.GetProperty("format").GetRawText(), options)!;
                var resetsAt = root.TryGetProperty("resetsAt", out var rEl) && rEl.ValueKind != JsonValueKind.Null ? rEl.GetDateTimeOffset() : (DateTimeOffset?)null;
                var periodMs = root.TryGetProperty("periodDurationMs", out var pEl) && pEl.ValueKind != JsonValueKind.Null ? pEl.GetInt64() : (long?)null;
                return new MetricLine.Progress(
                    label,
                    root.GetProperty("used").GetDouble(),
                    root.GetProperty("limit").GetDouble(),
                    format,
                    resetsAt,
                    periodMs,
                    GetStringOrNull(root, "colorHex"));

            case "badge":
                return new MetricLine.Badge(
                    label,
                    root.GetProperty("text").GetString() ?? "",
                    GetStringOrNull(root, "colorHex"),
                    GetStringOrNull(root, "subtitle"));

            case "chart":
                var points = root.GetProperty("points").EnumerateArray()
                    .Select(p => JsonSerializer.Deserialize<MetricChartPoint>(p.GetRawText(), options)!)
                    .ToList();
                return new MetricLine.Chart(label, points, GetStringOrNull(root, "note"));

            default:
                throw new JsonException($"Unknown MetricLine type discriminator: {type}");
        }
    }

    public override void Write(Utf8JsonWriter writer, MetricLine value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("label", value.Label);

        switch (value)
        {
            case MetricLine.Text t:
                writer.WriteString("type", "text");
                writer.WriteString("value", t.Value);
                WriteOptionalString(writer, "colorHex", t.ColorHex);
                WriteOptionalString(writer, "subtitle", t.Subtitle);
                break;

            case MetricLine.Values v:
                writer.WriteString("type", "values");
                writer.WritePropertyName("values");
                JsonSerializer.Serialize(writer, v.ValuesList, options);
                WriteOptionalString(writer, "colorHex", v.ColorHex);
                if (v.ExpiriesAt is { Count: > 0 })
                {
                    writer.WritePropertyName("expiriesAt");
                    writer.WriteStartArray();
                    foreach (var e in v.ExpiriesAt) writer.WriteStringValue(e);
                    writer.WriteEndArray();
                }
                if (v.UnknownModels is { Count: > 0 })
                {
                    writer.WritePropertyName("unknownModels");
                    JsonSerializer.Serialize(writer, v.UnknownModels, options);
                }
                if (v.ModelBreakdown is not null)
                {
                    writer.WritePropertyName("modelBreakdown");
                    JsonSerializer.Serialize(writer, v.ModelBreakdown, options);
                }
                break;

            case MetricLine.Progress p:
                writer.WriteString("type", "progress");
                writer.WriteNumber("used", p.Used);
                writer.WriteNumber("limit", p.Limit);
                writer.WritePropertyName("format");
                JsonSerializer.Serialize(writer, p.Format, options);
                if (p.ResetsAt is { } resetsAt) writer.WriteString("resetsAt", resetsAt);
                if (p.PeriodDurationMs is { } periodMs) writer.WriteNumber("periodDurationMs", periodMs);
                WriteOptionalString(writer, "colorHex", p.ColorHex);
                break;

            case MetricLine.Badge b:
                writer.WriteString("type", "badge");
                writer.WriteString("text", b.BadgeText);
                WriteOptionalString(writer, "colorHex", b.ColorHex);
                WriteOptionalString(writer, "subtitle", b.Subtitle);
                break;

            case MetricLine.Chart c:
                writer.WriteString("type", "chart");
                writer.WritePropertyName("points");
                JsonSerializer.Serialize(writer, c.Points, options);
                WriteOptionalString(writer, "note", c.Note);
                break;
        }

        writer.WriteEndObject();
    }

    private static string? GetStringOrNull(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void WriteOptionalString(Utf8JsonWriter writer, string key, string? value)
    {
        if (value is not null) writer.WriteString(key, value);
    }
}

public sealed class ProgressFormatJsonConverter : JsonConverter<ProgressFormat>
{
    public override ProgressFormat? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var kind = root.GetProperty("kind").GetString();
        return kind switch
        {
            "percent" => ProgressFormat.PercentValue,
            "dollars" => ProgressFormat.DollarsValue,
            "count" => ProgressFormat.CountValue(root.GetProperty("suffix").GetString() ?? ""),
            _ => throw new JsonException($"Unknown ProgressFormat kind: {kind}")
        };
    }

    public override void Write(Utf8JsonWriter writer, ProgressFormat value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case ProgressFormat.Percent:
                writer.WriteString("kind", "percent");
                break;
            case ProgressFormat.Dollars:
                writer.WriteString("kind", "dollars");
                break;
            case ProgressFormat.Count c:
                writer.WriteString("kind", "count");
                writer.WriteString("suffix", c.Suffix);
                break;
        }
        writer.WriteEndObject();
    }
}
