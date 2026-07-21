using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Services;

namespace AIUsage.Core.Tests.Services;

public class LocalUsageApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);

    private static ProviderSnapshot Snapshot(string id, string display, List<MetricLine> lines, string? plan = "Pro") =>
        new(id, display, plan, lines, Now);

    private static LocalUsageApi.State BasicState(IReadOnlyList<string>? enabled = null, IReadOnlyDictionary<string, ProviderSnapshot>? snapshots = null, IReadOnlySet<string>? known = null)
    {
        var snap = snapshots ?? new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = Snapshot("claude", "Claude", new List<MetricLine> { new MetricLine.Progress("Session", 25, 100, ProgressFormat.PercentValue) })
        };
        return new LocalUsageApi.State(
            EnabledOrderedIds: enabled ?? new List<string> { "claude" },
            KnownIds: known ?? new HashSet<string> { "claude", "codex" },
            Snapshots: snap);
    }

    [Fact]
    public void Respond_Options_Returns204NoBody()
    {
        var response = LocalUsageApi.Respond("OPTIONS", "/v1/limits", BasicState());
        Assert.Equal(204, response.Status);
        Assert.Null(response.Body);
    }

    [Fact]
    public void Respond_UnknownRoute_Returns404NotFound()
    {
        var response = LocalUsageApi.Respond("GET", "/nope", BasicState());
        Assert.Equal(404, response.Status);
        Assert.Contains("not_found", System.Text.Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public void Respond_LimitsCollection_NonGet_Returns405()
    {
        var response = LocalUsageApi.Respond("POST", "/v1/limits", BasicState());
        Assert.Equal(405, response.Status);
        Assert.Contains("method_not_allowed", System.Text.Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public void Respond_UsageCollection_ReturnsArrayOfSnapshots()
    {
        var response = LocalUsageApi.Respond("GET", "/v1/usage", BasicState());
        Assert.Equal(200, response.Status);
        var json = JsonDocument.Parse(response.Body!).RootElement;
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(1, json.GetArrayLength());
        Assert.Equal("claude", json[0].GetProperty("providerId").GetString());
        Assert.Equal("Claude", json[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public void Respond_UsageCollection_EmptyWhenNoSnapshots()
    {
        var state = BasicState(enabled: new List<string> { "codex" }, snapshots: new Dictionary<string, ProviderSnapshot>());
        var response = LocalUsageApi.Respond("GET", "/v1/usage", state);
        var json = JsonDocument.Parse(response.Body!).RootElement;
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public void Respond_UsageSingleToken_UnknownToken_Returns404()
    {
        var response = LocalUsageApi.Respond("GET", "/v1/usage/nope", BasicState());
        Assert.Equal(404, response.Status);
    }

    [Fact]
    public void Respond_UsageSingleToken_KnownButNoSnapshot_ReturnsEmptyArray()
    {
        var state = BasicState(known: new HashSet<string> { "claude", "codex" }, snapshots: new Dictionary<string, ProviderSnapshot>());
        var response = LocalUsageApi.Respond("GET", "/v1/usage/codex", state);
        Assert.Equal(200, response.Status);
        var json = JsonDocument.Parse(response.Body!).RootElement;
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public void Respond_LimitsSingleToken_KnownProvider_ReturnsEnvelope()
    {
        var response = LocalUsageApi.Respond("GET", "/v1/limits/claude", BasicState());
        Assert.Equal(200, response.Status);
        var json = JsonDocument.Parse(response.Body!).RootElement;
        Assert.Equal("openusage.limits.v1", json.GetProperty("schema").GetString());
        Assert.True(json.GetProperty("providers").TryGetProperty("claude", out _));
    }

    [Fact]
    public void WireLine_ProgressType_SerializesUsedLimitFormat()
    {
        var state = BasicState();
        var response = LocalUsageApi.Respond("GET", "/v1/usage", state);
        var json = JsonDocument.Parse(response.Body!).RootElement;
        var line = json[0].GetProperty("lines")[0];
        Assert.Equal("progress", line.GetProperty("type").GetString());
        Assert.Equal(25, line.GetProperty("used").GetDouble());
        Assert.Equal(100, line.GetProperty("limit").GetDouble());
        Assert.Equal("percent", line.GetProperty("format").GetProperty("kind").GetString());
    }

    [Fact]
    public void WireLine_ValuesType_SerializesAsLegacyTextShape()
    {
        var snapshots = new Dictionary<string, ProviderSnapshot>
        {
            ["openrouter"] = Snapshot("openrouter", "OpenRouter", new List<MetricLine>
            {
                new MetricLine.Values("Balance", new List<MetricValue> { new(42.5, MetricKind.Dollars) })
            })
        };
        var state = BasicState(enabled: new List<string> { "openrouter" }, snapshots: snapshots, known: new HashSet<string> { "openrouter" });
        var response = LocalUsageApi.Respond("GET", "/v1/usage", state);
        var json = JsonDocument.Parse(response.Body!).RootElement;
        var line = json[0].GetProperty("lines")[0];
        Assert.Equal("text", line.GetProperty("type").GetString());
        Assert.Equal("Balance", line.GetProperty("label").GetString());
        Assert.Contains("42.50", line.GetProperty("value").GetString());
    }

    [Fact]
    public void WireLine_BadgeType_SerializesTextAndColor()
    {
        var snapshots = new Dictionary<string, ProviderSnapshot>
        {
            ["grok"] = Snapshot("grok", "Grok", new List<MetricLine> { new MetricLine.Badge("Pay as you go", "20 cap", "#22c55e") })
        };
        var state = BasicState(enabled: new List<string> { "grok" }, snapshots: snapshots, known: new HashSet<string> { "grok" });
        var response = LocalUsageApi.Respond("GET", "/v1/usage", state);
        var json = JsonDocument.Parse(response.Body!).RootElement;
        var line = json[0].GetProperty("lines")[0];
        Assert.Equal("badge", line.GetProperty("type").GetString());
        Assert.Equal("20 cap", line.GetProperty("text").GetString());
        Assert.Equal("#22c55e", line.GetProperty("color").GetString());
    }

    [Fact]
    public void WireLine_ChartType_SerializesPointsAndNote()
    {
        var snapshots = new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = Snapshot("claude", "Claude", new List<MetricLine>
            {
                new MetricLine.Chart("Usage Trend", new List<MetricChartPoint> { new(1200000, "Mar 25", "1.2M tokens") }, "note")
            })
        };
        var state = BasicState(snapshots: snapshots);
        var response = LocalUsageApi.Respond("GET", "/v1/usage", state);
        var json = JsonDocument.Parse(response.Body!).RootElement;
        var line = json[0].GetProperty("lines")[0];
        Assert.Equal("barChart", line.GetProperty("type").GetString());
        Assert.Equal(1, line.GetProperty("points").GetArrayLength());
        Assert.Equal("note", line.GetProperty("note").GetString());
    }
}
