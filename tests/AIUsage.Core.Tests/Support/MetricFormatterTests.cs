using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Tests.Support;

public class MetricFormatterTests
{
    [Theory]
    [InlineData(0, "0%")]
    [InlineData(48.4, "48%")]
    [InlineData(48.6, "49%")]
    [InlineData(100, "100%")]
    [InlineData(150, "100%")]   // clamped
    [InlineData(-10, "0%")]     // clamped
    public void Number_Percent_ClampsAndRounds(double value, string expected)
    {
        Assert.Equal(expected, MetricFormatter.Number(value, MetricKind.Percent, MetricFormatter.Style.Row));
    }

    [Fact]
    public void Number_Dollars_Full_NeverCompacts()
    {
        var result = MetricFormatter.Number(2500, MetricKind.Dollars, MetricFormatter.Style.Full);
        Assert.Equal("$2,500.00", result);
    }

    [Fact]
    public void Number_Dollars_Row_CompactsAboveOneThousand()
    {
        var result = MetricFormatter.Number(2500, MetricKind.Dollars, MetricFormatter.Style.Row);
        Assert.Equal("$2.5K", result);
    }

    [Fact]
    public void Number_Dollars_Tray_RoundsToWholeDollar()
    {
        var result = MetricFormatter.Number(4.08, MetricKind.Dollars, MetricFormatter.Style.Tray);
        Assert.Equal("$4", result);
    }

    [Fact]
    public void Number_Dollars_Row_SmallAmount_ShowsCents()
    {
        var result = MetricFormatter.Number(4.08, MetricKind.Dollars, MetricFormatter.Style.Row);
        Assert.Equal("$4.08", result);
    }

    [Theory]
    [InlineData(1_200_000, "1.2M")]
    [InlineData(3_400_000_000, "3.4B")]
    [InlineData(52_871_034, "52.9M")]
    public void Number_Count_CompactsLargeValues(double value, string expected)
    {
        var result = MetricFormatter.Number(value, MetricKind.Count, MetricFormatter.Style.Row);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Number_Count_Full_NeverCompacts()
    {
        var result = MetricFormatter.Number(1_200_000, MetricKind.Count, MetricFormatter.Style.Full);
        Assert.DoesNotContain("M", result);
    }

    [Fact]
    public void Number_Count_SmallValue_TrimsTrailingZero()
    {
        var result = MetricFormatter.Number(5, MetricKind.Count, MetricFormatter.Style.Row);
        Assert.Equal("5", result);
    }

    [Fact]
    public void StringFor_AppendsLabel_WhenPresent()
    {
        var value = new MetricValue(24.68, MetricKind.Dollars);
        var withLabel = new MetricValue(33128149, MetricKind.Count, "tokens");
        Assert.DoesNotContain(" ", MetricFormatter.StringFor(value, MetricFormatter.Style.Row).TrimStart('$'));
        Assert.EndsWith("tokens", MetricFormatter.StringFor(withLabel, MetricFormatter.Style.Row));
    }

    [Fact]
    public void CostPerMtok_AppendsSuffix()
    {
        var result = MetricFormatter.CostPerMtok(1.37, MetricFormatter.Style.Row);
        Assert.EndsWith("/MTok", result);
        Assert.StartsWith("$1.37", result);
    }
}
