using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Services;

/// <summary>
/// The limits envelope and CLI warnings must expose the same provider issues: fatal refresh errors
/// plus the optional nonfatal <see cref="ProviderSnapshot.LimitsError"/>, with fatal winning and the
/// requested/enabled provider filter preserved.
/// </summary>
public class LimitIssuesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Provider OpenCode = new("opencode", "OpenCode", "opencode");
    private static readonly Provider Codex = new("codex", "Codex", "codex");

    private static ProviderSnapshot Snapshot(string id, string display, string? limitsError, params MetricLine[] lines) =>
        new(id, display, null, lines.ToList(), Now, LimitsError: limitsError,
            DataVersion: id == "opencode" ? OpenCodeQuotaCompatibility.CurrentDataVersion : null);

    private static WidgetDescriptor SessionLimit() =>
        WidgetDescriptorFactories.Percent("opencode.session", OpenCode, "Session")
            .ExportingLimit("session", unit: "usd");

    [Fact]
    public void Encode_IncludesSnapshotLimitsErrorAndKeepsProviderResources()
    {
        var snapshot = Snapshot("opencode", "OpenCode", "OpenCode Console rejected the saved sign-in.",
            new MetricLine.Progress("Session", 0.72, 12, ProgressFormat.DollarsValue));
        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "opencode" },
            KnownIds: new HashSet<string> { "opencode" },
            Snapshots: new Dictionary<string, ProviderSnapshot> { ["opencode"] = snapshot },
            LimitDescriptors: new Dictionary<string, List<WidgetDescriptor>>
            {
                ["opencode"] = new List<WidgetDescriptor> { SessionLimit() }
            });

        var json = JsonDocument.Parse(LocalLimitsApi.Encode(new List<string> { "opencode" }, state)).RootElement;

        var provider = json.GetProperty("providers").GetProperty("opencode");
        Assert.Equal(12, provider.GetProperty("resources").GetProperty("session").GetProperty("limit").GetDouble());
        var errors = json.GetProperty("errors");
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("opencode", errors[0].GetProperty("providerId").GetString());
        Assert.Equal("OpenCode Console rejected the saved sign-in.", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public void Encode_FatalErrorWinsAndEveryProviderAppearsOnlyOnce()
    {
        var snapshot = Snapshot("opencode", "OpenCode", "nonfatal quota issue");
        var codexSnapshot = Snapshot("codex", "Codex", "codex nonfatal issue");
        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "opencode" },
            KnownIds: new HashSet<string> { "opencode", "codex" },
            Snapshots: new Dictionary<string, ProviderSnapshot>
            {
                ["opencode"] = snapshot,
                ["codex"] = codexSnapshot
            },
            Errors: new Dictionary<string, string> { ["opencode"] = "fatal refresh failure" });

        var json = JsonDocument.Parse(
            LocalLimitsApi.Encode(new List<string> { "opencode", "opencode" }, state)).RootElement;

        var errors = json.GetProperty("errors");
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("opencode", errors[0].GetProperty("providerId").GetString());
        Assert.Equal("fatal refresh failure", errors[0].GetProperty("message").GetString());
        Assert.False(json.GetProperty("providers").TryGetProperty("codex", out _));
    }

    [Fact]
    public async Task UsageReader_CachedSnapshotStillWarnsAboutLimitIssue()
    {
        var snapshot = Snapshot("opencode", "OpenCode", "quota still unavailable",
            new MetricLine.Progress("Session", 0.72, 12, ProgressFormat.DollarsValue));
        var provider = new ScriptedProviderRuntime("opencode", snapshot);
        var cache = new ProviderSnapshotCache(
            new InMemorySettingsStore(), allowsPersistedFreshness: true, now: () => DateTimeOffset.UtcNow);
        cache.Store(snapshot with { RefreshedAt = DateTimeOffset.UtcNow });

        var result = await new UsageReader(new List<IProviderRuntime> { provider }, cache)
            .ReadAsync("opencode");

        Assert.Equal(0, provider.RefreshCount);
        Assert.Contains("opencode: quota still unavailable", result.Warnings);
        var errors = JsonDocument.Parse(result.Json).RootElement.GetProperty("errors");
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("quota still unavailable", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UsageReader_FreshRefreshWarnsOnlyForTheRequestedProvider()
    {
        var openCode = new ScriptedProviderRuntime("opencode",
            Snapshot("opencode", "OpenCode", "opencode quota unavailable"));
        var codex = new ScriptedProviderRuntime("codex",
            Snapshot("codex", "Codex", "codex should not appear"));
        var cache = new ProviderSnapshotCache(
            new InMemorySettingsStore(), allowsPersistedFreshness: true, now: () => DateTimeOffset.UtcNow);

        var result = await new UsageReader(new List<IProviderRuntime> { openCode, codex }, cache)
            .ReadAsync("opencode");

        Assert.Equal(1, openCode.RefreshCount);
        Assert.Equal(0, codex.RefreshCount);
        Assert.Equal(new[] { "opencode: opencode quota unavailable" }, result.Warnings);

        var errors = JsonDocument.Parse(result.Json).RootElement.GetProperty("errors");
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("opencode", errors[0].GetProperty("providerId").GetString());
    }

    [Fact]
    public void LegacyCacheWithoutMarker_StillLoadsLocalDataButIsNotACacheHit()
    {
        const string legacyJson = """
            {
              "opencode": {
                "providerId": "opencode",
                "displayName": "OpenCode",
                "plan": "Go",
                "lines": [
                  {
                    "label": "Session",
                    "type": "progress",
                    "used": 0.72,
                    "limit": 12,
                    "format": { "kind": "dollars" }
                  }
                ],
                "refreshedAt": "2026-10-22T12:00:00+00:00",
                "usageHistory": null,
                "warning": null,
                "errorCategoryValue": null
              }
            }
            """;
        var settings = new InMemorySettingsStore();
        settings.SetString("aiusage.providerSnapshots.v1", legacyJson);
        var cache = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);

        var snapshot = cache.LoadSnapshots(new[] { "opencode" })["opencode"];

        Assert.Equal("OpenCode", snapshot.DisplayName);
        Assert.DoesNotContain(snapshot.Lines, line => line.Label == "Session");
        Assert.Null(snapshot.Plan);
        Assert.Equal(OpenCodeQuotaCompatibility.RefreshRequiredLimitsError, snapshot.LimitsError);
        Assert.Contains(snapshot.Lines.OfType<MetricLine.Badge>(),
            badge => badge.Label == "Go Quota" && badge.BadgeText == "Refresh Required");

        // The legacy timestamp stays intact for local data, but it can never satisfy a cache hit.
        Assert.Equal(Now, snapshot.RefreshedAt);
        Assert.Null(cache.Snapshot("opencode"));
    }
}
