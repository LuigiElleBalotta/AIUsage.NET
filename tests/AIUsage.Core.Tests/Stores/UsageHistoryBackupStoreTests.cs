using AIUsage.Core.Models;
using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;

namespace AIUsage.Core.Tests.Stores;

public class UsageHistoryBackupStoreTests : IDisposable
{
    private readonly string _tempDir;

    public UsageHistoryBackupStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AIUsageBackupTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);

    private static ProviderUsageHistory MakeHistory(long tokens = 1000, double cost = 2.5) => new(
        new DailyUsageSeries(new List<DailyUsageEntry> { new("2026-07-20", tokens, cost) }));

    private static ProviderSnapshotCache MakeCacheWithSnapshot(string providerId, ProviderUsageHistory? history)
    {
        var cache = new ProviderSnapshotCache(new InMemorySettingsStore(), allowsPersistedFreshness: true, now: () => Now);
        var snapshot = new ProviderSnapshot(providerId, providerId, "Pro", new List<MetricLine>(), Now, history);
        cache.Store(snapshot);
        return cache;
    }

    [Fact]
    public void Export_NoHistoryAnywhere_ThrowsNoHistoryToExport()
    {
        var cache = MakeCacheWithSnapshot("claude", history: null);
        var path = Path.Combine(_tempDir, "backup.json");

        var ex = Assert.Throws<UsageHistoryBackupException>(() => UsageHistoryBackupStore.Export(cache, path));
        Assert.Equal(UsageHistoryBackupError.NoHistoryToExport, ex.Kind);
    }

    [Fact]
    public void Export_WritesFileWithProviderHistory()
    {
        var cache = MakeCacheWithSnapshot("claude", MakeHistory());
        var path = Path.Combine(_tempDir, "backup.json");

        UsageHistoryBackupStore.Export(cache, path);

        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains("aiusage.history.v1", content);
        Assert.Contains("claude", content);
    }

    [Fact]
    public void Import_MergesIntoExistingCachedSnapshot()
    {
        var exportCache = MakeCacheWithSnapshot("claude", MakeHistory(tokens: 5000, cost: 10));
        var path = Path.Combine(_tempDir, "backup.json");
        UsageHistoryBackupStore.Export(exportCache, path);

        // Import target already has a (smaller) cached history for claude.
        var importCache = MakeCacheWithSnapshot("claude", MakeHistory(tokens: 100, cost: 0.5));
        var mergedCount = UsageHistoryBackupStore.Import(importCache, path);

        Assert.Equal(1, mergedCount);
        var merged = importCache.Snapshot("claude")!.UsageHistory!;
        // The higher token-count day (5000, from the import) should win over the smaller local one (100).
        Assert.Equal(5000, merged.Series.Daily.Single(d => d.Date == "2026-07-20").TotalTokens);
    }

    [Fact]
    public void Import_ProviderWithNoCachedSnapshot_IsSkipped()
    {
        var exportCache = new ProviderSnapshotCache(new InMemorySettingsStore(), allowsPersistedFreshness: true, now: () => Now);
        var snapshotWithHistory = new ProviderSnapshot("codex", "Codex", "Free", new List<MetricLine>(), Now, MakeHistory());
        exportCache.Store(snapshotWithHistory);
        var path = Path.Combine(_tempDir, "backup.json");
        UsageHistoryBackupStore.Export(exportCache, path);

        // Import target has never refreshed codex at all - no snapshot to attach history to.
        var importCache = new ProviderSnapshotCache(new InMemorySettingsStore(), allowsPersistedFreshness: true, now: () => Now);
        var mergedCount = UsageHistoryBackupStore.Import(importCache, path);

        Assert.Equal(0, mergedCount);
    }

    [Fact]
    public void Import_UnsupportedSchema_Throws()
    {
        var path = Path.Combine(_tempDir, "bad.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, """{"schema":"aiusage.history.v99","deviceName":"x","exportedAt":"2026-07-21T00:00:00Z","providers":{}}""");

        var cache = new ProviderSnapshotCache(new InMemorySettingsStore());
        var ex = Assert.Throws<UsageHistoryBackupException>(() => UsageHistoryBackupStore.Import(cache, path));
        Assert.Equal(UsageHistoryBackupError.UnsupportedSchema, ex.Kind);
    }

    [Fact]
    public void Import_InvalidJson_ThrowsInvalidFile()
    {
        var path = Path.Combine(_tempDir, "garbage.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "not json at all");

        var cache = new ProviderSnapshotCache(new InMemorySettingsStore());
        var ex = Assert.Throws<UsageHistoryBackupException>(() => UsageHistoryBackupStore.Import(cache, path));
        Assert.Equal(UsageHistoryBackupError.InvalidFile, ex.Kind);
    }

    [Fact]
    public void Import_MissingFile_ThrowsInvalidFile()
    {
        var cache = new ProviderSnapshotCache(new InMemorySettingsStore());
        var ex = Assert.Throws<UsageHistoryBackupException>(() => UsageHistoryBackupStore.Import(cache, Path.Combine(_tempDir, "nope.json")));
        Assert.Equal(UsageHistoryBackupError.InvalidFile, ex.Kind);
    }
}

public class ProviderSnapshotCacheMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MergeUsageHistory_NoExistingSnapshot_ReturnsFalse()
    {
        var cache = new ProviderSnapshotCache(new InMemorySettingsStore());
        var history = new ProviderUsageHistory(new DailyUsageSeries(new List<DailyUsageEntry>()));
        Assert.False(cache.MergeUsageHistory("claude", history));
    }

    [Fact]
    public void MergeUsageHistory_NoExistingHistory_AdoptsImported()
    {
        var cache = new ProviderSnapshotCache(new InMemorySettingsStore(), allowsPersistedFreshness: true, now: () => Now);
        cache.Store(new ProviderSnapshot("claude", "Claude", "Pro", new List<MetricLine>(), Now));

        var imported = new ProviderUsageHistory(new DailyUsageSeries(new List<DailyUsageEntry> { new("2026-07-01", 500, 1.0) }));
        var merged = cache.MergeUsageHistory("claude", imported);

        Assert.True(merged);
        Assert.Equal(500, cache.Snapshot("claude")!.UsageHistory!.Series.Daily.Single().TotalTokens);
    }

    [Fact]
    public void MergeUsageHistory_UnionsNonOverlappingDays()
    {
        var cache = new ProviderSnapshotCache(new InMemorySettingsStore(), allowsPersistedFreshness: true, now: () => Now);
        var existingHistory = new ProviderUsageHistory(new DailyUsageSeries(new List<DailyUsageEntry> { new("2026-07-19", 100) }));
        cache.Store(new ProviderSnapshot("claude", "Claude", "Pro", new List<MetricLine>(), Now, existingHistory));

        var imported = new ProviderUsageHistory(new DailyUsageSeries(new List<DailyUsageEntry> { new("2026-07-20", 200) }));
        cache.MergeUsageHistory("claude", imported);

        var days = cache.Snapshot("claude")!.UsageHistory!.Series.Daily;
        Assert.Equal(2, days.Count);
        Assert.Contains(days, d => d.Date == "2026-07-19" && d.TotalTokens == 100);
        Assert.Contains(days, d => d.Date == "2026-07-20" && d.TotalTokens == 200);
    }

    [Fact]
    public void LegacyPersistedJsonAndLarge64BitCounts_RoundTripThroughSnapshotCacheAndHistoryMerge()
    {
        var settings = new InMemorySettingsStore();
        // Cached JSON written when TotalTokens was a 32-bit int. The snapshot carries the current
        // OpenCode quota provenance so this test stays about the 64-bit numeric round-trip, not the
        // legacy-quota migration (covered separately).
        settings.SetString("aiusage.providerSnapshots.v1", """
            {
              "opencode": {
                "providerId": "opencode",
                "displayName": "OpenCode",
                "plan": "Go",
                "lines": [],
                "refreshedAt": "2026-07-21T09:00:00+00:00",
                "dataVersion": "opencode-console-quota-v1",
                "usageHistory": {
                  "series": {
                    "daily": [
                      { "date": "2026-07-20", "totalTokens": 123456, "costUSD": 1.25 }
                    ]
                  },
                  "modelUsage": {
                    "daily": [
                      {
                        "date": "2026-07-20",
                        "models": [
                          { "model": "claude-sonnet-4", "totalTokens": 123456, "costUSD": 1.25 }
                        ]
                      }
                    ]
                  }
                }
              }
            }
            """);

        var cache = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now);
        var loaded = cache.Snapshot("opencode");
        Assert.NotNull(loaded);
        Assert.Equal(123456L, loaded!.UsageHistory!.Series.Daily.Single().TotalTokens);

        var imported64Bit = new ProviderUsageHistory(
            new DailyUsageSeries(new List<DailyUsageEntry>
            {
                new("2026-07-20", 5_000_000_000L, 25.0)
            }),
            new ModelUsageSeries(new List<DailyModelUsageEntry>
            {
                new("2026-07-20", new List<ModelUsageEntry>
                {
                    new("claude-sonnet-4", 5_000_000_000L, 25.0)
                })
            }));

        Assert.True(cache.MergeUsageHistory("opencode", imported64Bit));

        // Reload from a fresh cache instance over the same settings store to verify JSON persistence.
        var reloaded = new ProviderSnapshotCache(settings, allowsPersistedFreshness: true, now: () => Now).Snapshot("opencode");
        Assert.Equal(5_000_000_000L, reloaded!.UsageHistory!.Series.Daily.Single().TotalTokens);
    }
}
