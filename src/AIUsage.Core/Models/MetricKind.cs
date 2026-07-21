namespace AIUsage.Core.Models;

/// <summary>
/// How a metric's number is formatted. Mirrors the Swift edition's MetricKind.
/// </summary>
public enum MetricKind
{
    Percent,
    Dollars,
    Count
}

/// <summary>
/// How a bounded (.progress) row's number is formatted: percent, dollars, or a count with a unit suffix.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ProgressFormatJsonConverter))]
public abstract record ProgressFormat
{
    public sealed record Percent : ProgressFormat;
    public sealed record Dollars : ProgressFormat;
    public sealed record Count(string Suffix) : ProgressFormat;

    public MetricKind MetricKind => this switch
    {
        Percent => Models.MetricKind.Percent,
        Dollars => Models.MetricKind.Dollars,
        Count => Models.MetricKind.Count,
        _ => Models.MetricKind.Count
    };

    public string? CountSuffix => this is Count c ? c.Suffix : null;

    public static readonly ProgressFormat PercentValue = new Percent();
    public static readonly ProgressFormat DollarsValue = new Dollars();
    public static ProgressFormat CountValue(string suffix) => new Count(suffix);
}
