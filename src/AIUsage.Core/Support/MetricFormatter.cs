using System.Globalization;
using AIUsage.Core.Models;

namespace AIUsage.Core.Support;

/// <summary>
/// The single place a number becomes display text. Direct port of the Swift MetricFormatter.
/// </summary>
public static class MetricFormatter
{
    public enum Style
    {
        Tray,
        Row,
        Full
    }

    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    public static string Number(double value, MetricKind kind, Style style)
    {
        switch (kind)
        {
            case MetricKind.Percent:
                return $"{(int)Math.Round(ProviderParse.ClampPercent(value))}%";

            case MetricKind.Dollars:
                if (Math.Abs(value) >= 1000 && style != Style.Full)
                {
                    return "$" + CompactNumber(value);
                }
                return style switch
                {
                    Style.Tray => "$" + Math.Round(value, 0).ToString("N0", UsCulture),
                    _ => Formatters.Currency(value, 2)
                };

            case MetricKind.Count:
                if (style != Style.Full && Math.Abs(value) >= 1000)
                {
                    return CompactNumber(value);
                }
                return value.ToString("N1", UsCulture).TrimEnd('0').TrimEnd('.');

            default:
                return value.ToString(UsCulture);
        }
    }

    /// <summary>Compact notation similar to Swift's .notation(.compactName): 12.9K / 3.4M / 1.2B.</summary>
    private static string CompactNumber(double value)
    {
        var abs = Math.Abs(value);
        var sign = value < 0 ? "-" : "";
        (double scaled, string suffix) = abs switch
        {
            >= 1_000_000_000_000 => (abs / 1_000_000_000_000, "T"),
            >= 1_000_000_000 => (abs / 1_000_000_000, "B"),
            >= 1_000_000 => (abs / 1_000_000, "M"),
            >= 1_000 => (abs / 1_000, "K"),
            _ => (abs, "")
        };
        var formatted = Math.Round(scaled, 1).ToString("0.#", UsCulture);
        return sign + formatted + suffix;
    }

    public static string StringFor(MetricValue value, Style style)
    {
        var text = Number(value.Number, value.Kind, style);
        return string.IsNullOrEmpty(value.Label) ? text : $"{text} {value.Label}";
    }

    public static string CostPerMtok(double value, Style style) => Number(value, MetricKind.Dollars, style) + "/MTok";
}
