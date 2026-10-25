using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;
using System.Text.Json;
using Xunit;

namespace AIUsage.Core.Tests.Stores;

/// <summary>
/// Upgrade coverage for the persisted OpenCode snapshot cache. Older builds stored Session / Weekly /
/// Monthly meters inferred from local spend plus a "Go" plan claim in the same cache file the current
/// server-normalized Console quota uses. A recent legacy timestamp must not make that data look
/// authoritative: the quota claim is removed on read/write, local spend and history survive, the
/// snapshot stays ineligible for a cache hit, and a fresh Console refresh stamps the new marker.
/// </summary>
public class OpenCodeQuotaCacheUpgradeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private const string StorageKey = "aiusage.providerSnapshots.v1";

    private static readonly Provider OpenCode = new("opencode", "OpenCode", "opencode");

    private static string LegacyJson(string? dataVersion = null, string providerId = "opencode")
    {
        var marker = dataVersion is null ? "" : $",\n    \"dataVersion\": \"{dataVersion}\"";
        var quotaLimit = providerId == "opencode" ? 12 : 5;
        return $$"""
            {
              "{{providerId}}": {
                "providerId": "{{providerId}}",
                "displayName": "{{providerId}}",
                "plan": "Go",
                "lines": [
                  { "label": "Session", "type": "progress", "used": 0.72, "limit": {{quotaLimit}}, "format": { "kind": "dollars" } },
                  { "label": "Weekly", "type": "progress", "used": 5.7, "limit": 30, "format": { "kind": "dollars" } },
                  { "label": "Monthly", "type": "progress", "used": 5.4, "limit": 60, "format": { "kind": "dollars" } },
                  { "label": "Local Usage", "type": "badge", "text": "Partial Cost", "colorHex": "#F59E0B", "subtitle": "Some OpenCode messages have no recorded cost." },
                  {
                    "label": "Today",
                    "type": "values",
                    "values": [
                      { "number": 0.80, "kind": 1, "estimated": false },
                      { "number": 1500, "kind": 2, "label": "tokens", "estimated": false }
                    ]
                  },
                  {
                    "label": "Last 30 Days",
                    "type": "values",
                    "values": [
                      { "number": 2.40, "kind": 1, "estimated": false },
                      { "number": 4500, "kind": 2, "label": "tokens", "estimated": false }
                    ]
                  }
                ],
                "refreshedAt": "2026-10-23T12:00:00+00:00"{{marker}},
                "usageHistory": {
                  "series": {
                    "daily": [
                      { "date": "2026-10-22", "totalTokens": 1234, "costUSD": 1.25 }
                    ]
                  }
                }
              }
            }
            """;
    }

    private static ProviderSnapshotCache CacheWith(string json)
    {
        var settings = new InMemorySettingsStore();
        settings.SetString(StorageKey, json);
        return new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);
    }

    private static ProviderSnapshot Stored(ProviderSnapshotCache cache, string providerId = "opencode") =>
        cache.LoadSnapshots(new[] { providerId })[providerId];

    private static string[] LineLabels(ProviderSnapshot snapshot) =>
        snapshot.Lines.Select(line => line.Label).ToArray();

    private static ProviderSnapshot CurrentSnapshot(DateTimeOffset? refreshedAt = null) =>
        new("opencode", "OpenCode", "Go",
            new List<MetricLine>
            {
                new MetricLine.Progress("Session", 0.72, 12, ProgressFormat.DollarsValue),
                new MetricLine.Values("Today", new List<MetricValue>
                {
                    new(0.80, MetricKind.Dollars),
                    new(1500, MetricKind.Count, "tokens")
                })
            },
            refreshedAt ?? Now,
            DataVersion: OpenCodeQuotaCompatibility.CurrentDataVersion);

    [Fact]
    public void PreUpgradeJson_WithoutMarker_LosesOnlyTheQuotaClaimAndKeepsLocalData()
    {
        var cache = CacheWith(LegacyJson());

        var snapshot = Stored(cache);

        Assert.Equal(new[] { "Local Usage", "Today", "Last 30 Days", "Go Quota" }, LineLabels(snapshot));
        Assert.DoesNotContain(snapshot.Lines, line => line is MetricLine.Progress);
        Assert.Null(snapshot.Plan);
        Assert.Equal(Now, snapshot.RefreshedAt);
        Assert.Equal(OpenCodeQuotaCompatibility.RefreshRequiredLimitsError, snapshot.LimitsError);

        var migrationBadge = Assert.Single(snapshot.Lines.OfType<MetricLine.Badge>(),
            badge => badge.Label == OpenCodeUsageMapper.QuotaBadgeLabel);
        Assert.Equal("Refresh Required", migrationBadge.BadgeText);

        // Local spend and its qualifier survive untouched.
        var partialCost = Assert.Single(snapshot.Lines.OfType<MetricLine.Badge>(),
            badge => badge.Label == "Local Usage");
        Assert.Equal("Partial Cost", partialCost.BadgeText);
        var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
        Assert.Equal(0.80, today.ValuesList.Single(value => value.Kind == MetricKind.Dollars).Number);
        Assert.False(today.ValuesList.Single(value => value.Kind == MetricKind.Dollars).Estimated);

        // History survives for the usage-history backup/export path.
        Assert.Equal(1234, Assert.Single(snapshot.UsageHistory!.Series.Daily).TotalTokens);
        Assert.Equal(1234, Assert.Single(cache.AllSnapshots()["opencode"].UsageHistory!.Series.Daily).TotalTokens);

        // The recent legacy timestamp is not a cache hit.
        Assert.Null(cache.Snapshot("opencode"));
    }

    [Fact]
    public void UnsupportedMarker_IsTreatedAsLegacyAndReloadsIdempotently()
    {
        var settings = new InMemorySettingsStore();
        settings.SetString(StorageKey, LegacyJson(dataVersion: "opencode-console-quota-v0"));
        var first = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);
        var firstLoad = Stored(first);

        var second = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);
        var secondLoad = Stored(second);

        Assert.Equal(LineLabels(firstLoad), LineLabels(secondLoad));
        Assert.Equal(firstLoad.Lines.Count, secondLoad.Lines.Count);
        Assert.Single(secondLoad.Lines.OfType<MetricLine.Badge>(),
            badge => badge.Label == OpenCodeUsageMapper.QuotaBadgeLabel);
        Assert.Null(secondLoad.Plan);
        Assert.Equal(OpenCodeQuotaCompatibility.RefreshRequiredLimitsError, secondLoad.LimitsError);
        Assert.Null(second.Snapshot("opencode"));

        // A Save path (history merge) must also persist the sanitized, still-ineligible shape.
        var imported = new ProviderUsageHistory(new DailyUsageSeries(new List<DailyUsageEntry>
        {
            new("2026-10-21", 900, 0.5)
        }));
        Assert.True(second.MergeUsageHistory("opencode", imported));

        var third = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);
        var thirdLoad = Stored(third);
        Assert.Equal(LineLabels(secondLoad), LineLabels(thirdLoad));
        Assert.Equal(2, thirdLoad.UsageHistory!.Series.Daily.Count);
        Assert.Null(third.Snapshot("opencode"));
    }

    [Fact]
    public void Store_UnmarkedSnapshot_CannotReintroduceLegacyQuotaIntoCacheMemoryOrJson()
    {
        var settings = new InMemorySettingsStore();
        var cache = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);

        cache.Store(new ProviderSnapshot("opencode", "OpenCode", "Go",
            new List<MetricLine>
            {
                new MetricLine.Progress("Session", 0.72, 12, ProgressFormat.DollarsValue)
            },
            Now));

        Assert.Null(cache.Snapshot("opencode"));
        var sanitized = Stored(cache);
        Assert.DoesNotContain(sanitized.Lines, line => line is MetricLine.Progress);
        Assert.Null(sanitized.Plan);

        var reloaded = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);
        Assert.Null(reloaded.Snapshot("opencode"));
        Assert.DoesNotContain(Stored(reloaded).Lines, line => line is MetricLine.Progress);
    }

    [Fact]
    public void CurrentMarker_KeepsServerQuotaAndRemainsEligible()
    {
        var cache = CacheWith(LegacyJson(dataVersion: OpenCodeQuotaCompatibility.CurrentDataVersion));

        var snapshot = Stored(cache);

        Assert.Equal(new[] { "Session", "Weekly", "Monthly", "Local Usage", "Today", "Last 30 Days" },
            LineLabels(snapshot));
        Assert.Equal("Go", snapshot.Plan);
        Assert.Null(snapshot.LimitsError);
        Assert.DoesNotContain(snapshot.Lines.OfType<MetricLine.Badge>(),
            badge => badge.Label == "Go Quota");
        Assert.NotNull(cache.Snapshot("opencode"));
    }

    [Fact]
    public void PublicUsageWire_DoesNotExposeCacheProvenance()
    {
        var cache = CacheWith(LegacyJson(dataVersion: OpenCodeQuotaCompatibility.CurrentDataVersion));
        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "opencode" },
            KnownIds: new HashSet<string> { "opencode" },
            Snapshots: new Dictionary<string, ProviderSnapshot> { ["opencode"] = Stored(cache) });

        var response = LocalUsageApi.Respond("GET", "/v1/usage", state);
        var json = System.Text.Encoding.UTF8.GetString(response.Body!);

        Assert.DoesNotContain("dataVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenCode", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherProviders_WithoutMarker_AreUnaffected()
    {
        var cache = CacheWith(LegacyJson(providerId: "codex"));

        var snapshot = Stored(cache, "codex");

        Assert.Equal("Go", snapshot.Plan);
        Assert.Equal(3, snapshot.Lines.OfType<MetricLine.Progress>().Count());
        Assert.Null(snapshot.LimitsError);
        Assert.NotNull(cache.Snapshot("codex"));
    }

    [Fact]
    public async Task UsageReader_NonForceReadOfLegacyCache_RefreshesAndKeepsLocalDataOnFatalFailure()
    {
        var settings = new InMemorySettingsStore();
        settings.SetString(StorageKey, LegacyJson());
        var cache = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);

        var fatal = ProviderSnapshot.Error(OpenCode, new Exception("fatal refresh failure"), Now.AddMinutes(1));
        var runtime = new ScriptedProviderRuntime(OpenCode, fatal)
        {
            WidgetDescriptors = new OpenCodeProvider().WidgetDescriptors
        };

        var result = await new UsageReader(new List<IProviderRuntime> { runtime }, cache)
            .ReadAsync("opencode");

        // A fresh-looking legacy timestamp still triggers the Console refresh attempt.
        Assert.Equal(1, runtime.RefreshCount);

        // Fatal priority is retained: one warning/error, not the migration note.
        Assert.Equal(new[] { "opencode: fatal refresh failure" }, result.Warnings);
        var errors = JsonDocument.Parse(result.Json).RootElement.GetProperty("errors");
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("fatal refresh failure", errors[0].GetProperty("message").GetString());

        // Old quota is gone from the machine-facing resources even after the fatal failure.
        var provider = JsonDocument.Parse(result.Json).RootElement.GetProperty("providers").GetProperty("opencode");
        Assert.False(provider.GetProperty("resources").TryGetProperty("session", out _));
        Assert.False(provider.GetProperty("resources").TryGetProperty("weekly", out _));
        Assert.False(provider.GetProperty("resources").TryGetProperty("monthly", out _));

        // Local spend/history remain available in the retained sanitized snapshot.
        var retained = cache.LoadSnapshots(new[] { "opencode" })["opencode"];
        Assert.True(retained.Line("Today") is MetricLine.Values);
        Assert.Equal(1234, Assert.Single(retained.UsageHistory!.Series.Daily).TotalTokens);
    }

    [Fact]
    public async Task ForcedFatalRefreshAfterUpgrade_DoesNotResurrectLegacyQuota()
    {
        var settings = new InMemorySettingsStore();
        settings.SetString(StorageKey, LegacyJson());
        var cache = new ProviderSnapshotCache(settings, now: () => Now);

        var provider = new OpenCodeProvider();
        var fatal = ProviderSnapshot.Error(OpenCode, new Exception("refresh failed completely"), Now.AddMinutes(1));
        var runtime = new ScriptedProviderRuntime(OpenCode, fatal)
        {
            WidgetDescriptors = provider.WidgetDescriptors
        };
        var registry = new WidgetRegistry(new List<Provider> { OpenCode }, runtime.WidgetDescriptors);
        var store = new WidgetDataStore(registry, new List<IProviderRuntime> { runtime }, cache, now: () => Now);

        Assert.Equal(RefreshOutcome.Failed, await store.RefreshAsync("opencode", force: true));

        // No old inferred meter is presented as current, even as retained last-good data.
        Assert.False(store.Data(provider.WidgetDescriptors.Single(d => d.Id == "opencode.session")).HasData);
        Assert.Empty(store.Snapshots["opencode"].Lines.OfType<MetricLine.Progress>());

        var badges = store.StatusBadges("opencode");
        Assert.Contains(badges, badge => badge.Headline == "Refresh Failed");
        Assert.Contains(badges, badge => badge.Headline == "Refresh Required");

        // Local spend stays visible with its own value.
        var today = store.Data(provider.WidgetDescriptors.Single(d => d.Id == "opencode.today"));
        Assert.True(today.HasData);
        Assert.Equal("$0.80", today.Headline);
    }

    [Fact]
    public async Task SuccessfulRefresh_StampsMarkerClearsMigrationStatusAndThenHitsCache()
    {
        var settings = new InMemorySettingsStore();
        settings.SetString(StorageKey, LegacyJson());
        var cache = new ProviderSnapshotCache(settings, allowsPersistedFreshness: false, now: () => Now);

        var provider = new OpenCodeProvider();
        var runtime = new ScriptedProviderRuntime(OpenCode, CurrentSnapshot())
        {
            WidgetDescriptors = provider.WidgetDescriptors
        };
        var registry = new WidgetRegistry(new List<Provider> { OpenCode }, runtime.WidgetDescriptors);
        var store = new WidgetDataStore(registry, new List<IProviderRuntime> { runtime }, cache, now: () => Now);

        var outcome = await store.RefreshAsync("opencode");

        Assert.Equal(RefreshOutcome.Refreshed, outcome);
        Assert.Equal(OpenCodeQuotaCompatibility.CurrentDataVersion,
            store.Snapshots["opencode"].DataVersion);
        Assert.Null(store.Snapshots["opencode"].LimitsError);
        Assert.Equal("Go", store.Snapshots["opencode"].Plan);
        Assert.Equal("94% left",
            store.Data(provider.WidgetDescriptors.Single(d => d.Id == "opencode.session")).BoundedHeadline);
        Assert.DoesNotContain(store.StatusBadges("opencode"),
            badge => badge.Headline == "Refresh Required");

        // The persisted result is current data: a later process reads it with no extra refresh.
        var reloadedCache = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);
        Assert.NotNull(reloadedCache.Snapshot("opencode"));

        var secondRuntime = new ScriptedProviderRuntime(OpenCode, CurrentSnapshot(Now.AddMinutes(1)))
        {
            WidgetDescriptors = provider.WidgetDescriptors
        };
        var secondStore = new WidgetDataStore(registry, new List<IProviderRuntime> { secondRuntime },
            reloadedCache, now: () => Now);
        Assert.Equal(RefreshOutcome.CacheHit, await secondStore.RefreshAsync("opencode"));
        Assert.Equal(0, secondRuntime.RefreshCount);
    }
}
