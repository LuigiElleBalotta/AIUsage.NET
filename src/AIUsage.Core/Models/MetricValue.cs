namespace AIUsage.Core.Models;

/// <summary>
/// One measured number on a metric row, carried raw so formatting happens only at the display edge
/// (see MetricFormatter).
/// </summary>
public sealed record MetricValue(
    double Number,
    MetricKind Kind,
    string? Label = null,
    bool Estimated = false
);

/// <summary>
/// Which of a row's values a widget renders.
/// </summary>
public abstract record ValueSelection
{
    public sealed record All : ValueSelection;
    public sealed record OfKind(MetricKind Kind) : ValueSelection;

    public static readonly ValueSelection AllValues = new All();

    public IReadOnlyList<MetricValue> Apply(IReadOnlyList<MetricValue> values) => this switch
    {
        All => values,
        OfKind k => values.Where(v => v.Kind == k.Kind).ToList(),
        _ => values
    };
}
