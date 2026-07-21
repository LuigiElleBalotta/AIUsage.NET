using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Services;

namespace AIUsage.Core.Tests.Services;

public class LocalLimitsApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly Provider CodexProvider = new("codex", "Codex", "codex");

    private static ProviderSnapshot Snapshot(string id, string display, List<MetricLine> lines, string? plan = "Pro 20x") =>
        new(id, display, plan, lines, Now);

    [Fact]
    public void Encode_ProgressResource_ComputesRemainingAndUtilization()
    {
        var descriptor = WidgetDescriptorFactories.Percent("codex.session", CodexProvider, "Session")
            .ExportingLimit("session", unit: "percent");
        var snapshot = Snapshot("codex", "Codex", new List<MetricLine>
        {
            new MetricLine.Progress("Session", 42, 100, ProgressFormat.PercentValue, Now.AddHours(5), 18_000_000)
        });

        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "codex" },
            KnownIds: new HashSet<string> { "codex" },
            Snapshots: new Dictionary<string, ProviderSnapshot> { ["codex"] = snapshot },
            LimitDescriptors: new Dictionary<string, List<WidgetDescriptor>> { ["codex"] = new List<WidgetDescriptor> { descriptor } });

        var bytes = LocalLimitsApi.Encode(new List<string> { "codex" }, state);
        var json = JsonDocument.Parse(bytes).RootElement;

        Assert.Equal("openusage.limits.v1", json.GetProperty("schema").GetString());
        var resource = json.GetProperty("providers").GetProperty("codex").GetProperty("resources").GetProperty("session");
        Assert.Equal("consumption", resource.GetProperty("kind").GetString());
        Assert.Equal("percent", resource.GetProperty("unit").GetString());
        Assert.Equal(42, resource.GetProperty("used").GetDouble());
        Assert.Equal(100, resource.GetProperty("limit").GetDouble());
        Assert.Equal(58, resource.GetProperty("remaining").GetDouble());
        Assert.Equal(0.42, resource.GetProperty("utilization").GetDouble(), precision: 3);
        Assert.Equal(18000.0, resource.GetProperty("windowSeconds").GetDouble());
    }

    [Fact]
    public void Encode_ValueResource_BalanceKind_UsesAvailableField()
    {
        var descriptor = WidgetDescriptorFactories.DollarBalance("codex.creditValue", CodexProvider, "Credits", "left", metricLabel: "Credits")
            .ExportingLimit("creditValue", LimitResourceDescriptor.ResourceKind.Balance, "usd", new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Dollars));
        var snapshot = Snapshot("codex", "Codex", new List<MetricLine>
        {
            new MetricLine.Values("Credits", new List<MetricValue> { new(38.88, MetricKind.Dollars) })
        });

        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "codex" },
            KnownIds: new HashSet<string> { "codex" },
            Snapshots: new Dictionary<string, ProviderSnapshot> { ["codex"] = snapshot },
            LimitDescriptors: new Dictionary<string, List<WidgetDescriptor>> { ["codex"] = new List<WidgetDescriptor> { descriptor } });

        var bytes = LocalLimitsApi.Encode(new List<string> { "codex" }, state);
        var json = JsonDocument.Parse(bytes).RootElement;
        var resource = json.GetProperty("providers").GetProperty("codex").GetProperty("resources").GetProperty("creditValue");

        Assert.Equal("balance", resource.GetProperty("kind").GetString());
        Assert.Equal(38.88, resource.GetProperty("available").GetDouble());
        Assert.False(resource.TryGetProperty("used", out _));
    }

    [Fact]
    public void Encode_ValueResource_NoMatchingMetric_OmitsResource()
    {
        var descriptor = WidgetDescriptorFactories.Values("codex.rateLimitResets", CodexProvider, "Rate Limit Resets", metricLabel: "Rate Limit Resets")
            .ExportingLimit("rateLimitResets", LimitResourceDescriptor.ResourceKind.Balance, "resets", new LimitResourceDescriptor.ResourceSource.Value(MetricKind.Count, "available"));
        var snapshot = Snapshot("codex", "Codex", new List<MetricLine>
        {
            new MetricLine.Values("Rate Limit Resets", new List<MetricValue> { new(1, MetricKind.Count, "other-label") })
        });

        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "codex" },
            KnownIds: new HashSet<string> { "codex" },
            Snapshots: new Dictionary<string, ProviderSnapshot> { ["codex"] = snapshot },
            LimitDescriptors: new Dictionary<string, List<WidgetDescriptor>> { ["codex"] = new List<WidgetDescriptor> { descriptor } });

        var bytes = LocalLimitsApi.Encode(new List<string> { "codex" }, state);
        var json = JsonDocument.Parse(bytes).RootElement;
        var resources = json.GetProperty("providers").GetProperty("codex").GetProperty("resources");

        Assert.False(resources.TryGetProperty("rateLimitResets", out _));
    }

    [Fact]
    public void Encode_ProviderWithNoDescriptors_OmitsResourcesButKeepsProvider()
    {
        var snapshot = Snapshot("codex", "Codex", new List<MetricLine> { new MetricLine.Progress("Session", 1, 100, ProgressFormat.PercentValue) });
        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "codex" },
            KnownIds: new HashSet<string> { "codex" },
            Snapshots: new Dictionary<string, ProviderSnapshot> { ["codex"] = snapshot });

        var bytes = LocalLimitsApi.Encode(new List<string> { "codex" }, state);
        var json = JsonDocument.Parse(bytes).RootElement;
        var provider = json.GetProperty("providers").GetProperty("codex");
        Assert.Empty(provider.GetProperty("resources").EnumerateObject());
    }

    [Fact]
    public void Encode_MissingSnapshot_OmitsProviderEntirely()
    {
        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "codex" },
            KnownIds: new HashSet<string> { "codex" },
            Snapshots: new Dictionary<string, ProviderSnapshot>());

        var bytes = LocalLimitsApi.Encode(new List<string> { "codex" }, state);
        var json = JsonDocument.Parse(bytes).RootElement;
        Assert.False(json.GetProperty("providers").TryGetProperty("codex", out _));
    }

    [Fact]
    public void Encode_ErrorForProvider_AppearsInErrorsArray()
    {
        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "codex" },
            KnownIds: new HashSet<string> { "codex" },
            Snapshots: new Dictionary<string, ProviderSnapshot>(),
            Errors: new Dictionary<string, string> { ["codex"] = "boom" });

        var bytes = LocalLimitsApi.Encode(new List<string> { "codex" }, state);
        var json = JsonDocument.Parse(bytes).RootElement;
        var errors = json.GetProperty("errors");
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("codex", errors[0].GetProperty("providerId").GetString());
        Assert.Equal("boom", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public void Encode_StaleFlag_TrueWhenGeneratedAtPastExpiry()
    {
        var snapshot = Snapshot("codex", "Codex", new List<MetricLine> { new MetricLine.Progress("Session", 1, 100, ProgressFormat.PercentValue) });
        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "codex" },
            KnownIds: new HashSet<string> { "codex" },
            Snapshots: new Dictionary<string, ProviderSnapshot> { ["codex"] = snapshot },
            GeneratedAt: Now.AddMinutes(10)); // well past the 5-minute freshness interval

        var bytes = LocalLimitsApi.Encode(new List<string> { "codex" }, state);
        var json = JsonDocument.Parse(bytes).RootElement;
        Assert.True(json.GetProperty("providers").GetProperty("codex").GetProperty("stale").GetBoolean());
    }

    [Fact]
    public void Encode_CountFormatProgress_UsesSuffixAsUnit()
    {
        var descriptor = WidgetDescriptorFactories.BoundedCount("cursor.requests", new Provider("cursor", "Cursor", "cursor"), "Requests", 500, "requests")
            .ExportingLimit("requests", unit: "requests");
        var snapshot = Snapshot("cursor", "Cursor", new List<MetricLine>
        {
            new MetricLine.Progress("Requests", 30, 500, ProgressFormat.CountValue("requests"))
        });
        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "cursor" },
            KnownIds: new HashSet<string> { "cursor" },
            Snapshots: new Dictionary<string, ProviderSnapshot> { ["cursor"] = snapshot },
            LimitDescriptors: new Dictionary<string, List<WidgetDescriptor>> { ["cursor"] = new List<WidgetDescriptor> { descriptor } });

        var bytes = LocalLimitsApi.Encode(new List<string> { "cursor" }, state);
        var json = JsonDocument.Parse(bytes).RootElement;
        var resource = json.GetProperty("providers").GetProperty("cursor").GetProperty("resources").GetProperty("requests");
        Assert.Equal("requests", resource.GetProperty("unit").GetString());
    }
}
