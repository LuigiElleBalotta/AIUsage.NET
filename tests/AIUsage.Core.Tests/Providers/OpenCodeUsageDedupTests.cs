using AIUsage.Core.Providers;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

/// <summary>
/// Regression tests for cross-database message-ID deduplication: V2 always beats V1, the newest
/// copy within one generation wins, and empty winners stay as dedup candidates without creating
/// fabricated zero-usage days.
/// </summary>
public class OpenCodeUsageDedupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static OpenCodeUsageScanner Scanner(params string[] databases) =>
        new(new SqliteDataAccessor(), () => databases.ToList(), timeZone: TimeZoneInfo.Utc);

    private static double TotalCost(OpenCodeUsageScan scan) =>
        scan.LogScan.Series.Daily.Sum(d => d.CostUSD ?? 0);

    private static long TotalTokens(OpenCodeUsageScan scan) =>
        scan.LogScan.Series.Daily.Sum(d => d.TotalTokens);

    private static void InsertV1(
        OpenCodeTestDatabase db,
        string id,
        long createdMs,
        long? updatedMs,
        double? cost = 1.0,
        long? totalTokens = 100)
    {
        db.CreateLegacySchema();
        db.InsertLegacy(id, createdMs, cost: cost, totalTokens: totalTokens);
        SetUpdated(db, "message", id, updatedMs);
    }

    private static void InsertV2(
        OpenCodeTestDatabase db,
        string id,
        long createdMs,
        long? updatedMs,
        double? cost = 1.0,
        long input = 100)
    {
        db.CreateV2Schema();
        db.InsertV2(id, createdMs, cost: cost, input: input);
        SetUpdated(db, "session_message", id, updatedMs);
    }

    private static void SetUpdated(OpenCodeTestDatabase db, string table, string id, long? updatedMs) =>
        db.Execute(
            $"UPDATE {table} SET time_updated=@updated WHERE id=@id",
            ("@updated", updatedMs),
            ("@id", id));

    [Fact]
    public async Task V2AcrossDatabases_NewestTimeUpdatedWinsInBothPathOrders()
    {
        using var store = new OpenCodeTestStore();
        var older = store.Database("opencode-next.db");
        var newer = store.Database("opencode.db");
        var created = Now.AddHours(-1).ToUnixTimeMilliseconds();
        InsertV2(older, "shared", created, created + 1000, cost: 1.0, input: 100);
        InsertV2(newer, "shared", created, created + 2000, cost: 3.0, input: 300);

        foreach (var paths in new[] { new[] { older.Path, newer.Path }, new[] { newer.Path, older.Path } })
        {
            var scan = await Scanner(paths).ScanAsync(Now);

            Assert.NotNull(scan);
            Assert.Equal(3.0, TotalCost(scan!), 4);
            Assert.Equal(300, TotalTokens(scan!));
        }
    }

    [Fact]
    public async Task V1AcrossDatabases_NewestTimeUpdatedWinsInBothPathOrders()
    {
        using var store = new OpenCodeTestStore();
        var older = store.Database("opencode-next.db");
        var newer = store.Database("opencode.db");
        var created = Now.AddHours(-1).ToUnixTimeMilliseconds();
        InsertV1(older, "shared", created, created + 1000, cost: 1.0, totalTokens: 100);
        InsertV1(newer, "shared", created, created + 2000, cost: 3.0, totalTokens: 300);

        foreach (var paths in new[] { new[] { older.Path, newer.Path }, new[] { newer.Path, older.Path } })
        {
            var scan = await Scanner(paths).ScanAsync(Now);

            Assert.NotNull(scan);
            Assert.Equal(3.0, TotalCost(scan!), 4);
            Assert.Equal(300, TotalTokens(scan!));
        }
    }

    [Fact]
    public async Task V2WinsOverNewerDatedV1_RegardlessOfPathOrder()
    {
        using var store = new OpenCodeTestStore();
        var v1 = store.Database("opencode.db");
        var v2 = store.Database("opencode-next.db");
        var created = Now.AddHours(-2).ToUnixTimeMilliseconds();
        InsertV1(v1, "migrated", created, created + 5000, cost: 4.0, totalTokens: 400);
        InsertV2(v2, "migrated", created, created + 1000, cost: 1.0, input: 100);

        foreach (var paths in new[] { new[] { v1.Path, v2.Path }, new[] { v2.Path, v1.Path } })
        {
            var scan = await Scanner(paths).ScanAsync(Now);

            Assert.NotNull(scan);
            Assert.Equal(1.0, TotalCost(scan!), 4);
            Assert.Equal(100, TotalTokens(scan!));
        }
    }

    [Fact]
    public async Task EqualEffectiveTimestamps_BreakOnOrdinalGreaterSourcePath()
    {
        using var store = new OpenCodeTestStore();
        var lower = store.Database("a.db");
        var higher = store.Database("b.db");
        var time = Now.AddHours(-1).ToUnixTimeMilliseconds();
        InsertV2(lower, "shared", time, time, cost: 1.0, input: 100);
        InsertV2(higher, "shared", time, time, cost: 2.0, input: 200);

        foreach (var paths in new[] { new[] { lower.Path, higher.Path }, new[] { higher.Path, lower.Path } })
        {
            var scan = await Scanner(paths).ScanAsync(Now);

            Assert.NotNull(scan);
            Assert.Equal(2.0, TotalCost(scan!), 4);
            Assert.Equal(200, TotalTokens(scan!));
        }
    }

    [Fact]
    public async Task MissingTimeUpdatedColumn_FallsBackToTimeCreated()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.Execute("CREATE TABLE session_message (id TEXT PRIMARY KEY, session_id TEXT, type TEXT, seq INTEGER, time_created INTEGER, data TEXT);");
        db.Execute(
            "INSERT INTO session_message (id, session_id, type, seq, time_created, data) VALUES ('reduced', 's', 'assistant', 0, @time, @data);",
            ("@time", Now.AddHours(-1).ToUnixTimeMilliseconds()),
            ("@data", """{"model":{"providerID":"opencode-go","id":"m"},"cost":0.75,"tokens":{"input":30}}"""));

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(0.75, TotalCost(scan!), 4);
        Assert.Equal(30, TotalTokens(scan!));
    }

    [Fact]
    public async Task MissingTimeUpdatedColumn_LegacyTable_StillReads()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.Execute("CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT);");
        db.Execute(
            "INSERT INTO message (id, session_id, time_created, data) VALUES ('reduced-v1', 's', @time, @data);",
            ("@time", Now.AddHours(-1).ToUnixTimeMilliseconds()),
            ("@data", """{"role":"assistant","providerID":"opencode-go","modelID":"m","cost":0.5,"tokens":{"total":42}}"""));

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(0.5, TotalCost(scan!), 4);
        Assert.Equal(42, TotalTokens(scan!));
    }

    [Fact]
    public async Task NullTimeUpdated_FallsBackToTimeCreatedForSelection()
    {
        using var store = new OpenCodeTestStore();
        var older = store.Database("a.db");
        var newer = store.Database("b.db");
        InsertV2(older, "shared", Now.AddHours(-2).ToUnixTimeMilliseconds(), updatedMs: null, cost: 1.0, input: 100);
        InsertV2(newer, "shared", Now.AddHours(-1).ToUnixTimeMilliseconds(), updatedMs: null, cost: 3.0, input: 300);

        foreach (var paths in new[] { new[] { older.Path, newer.Path }, new[] { newer.Path, older.Path } })
        {
            var scan = await Scanner(paths).ScanAsync(Now);

            Assert.NotNull(scan);
            Assert.Equal(3.0, TotalCost(scan!), 4);
            Assert.Equal(300, TotalTokens(scan!));
        }
    }

    [Fact]
    public async Task EmptyV2_SuppressesNonEmptyV1WithSameId()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateLegacySchema();
        db.CreateV2Schema();
        var time = Now.AddHours(-1).ToUnixTimeMilliseconds();
        db.InsertLegacy("migrated", time, cost: 4.0, totalTokens: 400);
        db.InsertV2("migrated", time, cost: null, input: 0);
        db.InsertV2("control", time, cost: 0.5, input: 50);

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(0.5, TotalCost(scan!), 4);
        Assert.Equal(50, TotalTokens(scan!));
    }

    [Fact]
    public async Task EmptyNewerV2_SuppressesOlderV2AndFabricatesNoDay()
    {
        using var store = new OpenCodeTestStore();
        var older = store.Database("a.db");
        var newer = store.Database("b.db");
        var created = Now.AddHours(-1).ToUnixTimeMilliseconds();
        InsertV2(older, "shared", created, created + 1000, cost: 2.0, input: 200);
        InsertV2(newer, "shared", created, created + 2000, cost: null, input: 0);

        var scan = await Scanner(older.Path, newer.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Empty(scan!.LogScan.Series.Daily);
        Assert.False(scan.Report.HasHostedUsage);
    }

    [Fact]
    public async Task ZeroCostAndTokenOnlyRows_AreRetained()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        var time = Now.AddHours(-1).ToUnixTimeMilliseconds();
        InsertV2(db, "known-zero", time, time, cost: 0, input: 10);
        InsertV2(db, "token-only", time, time, cost: null, input: 20);

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        var day = Assert.Single(scan!.LogScan.Series.Daily);
        Assert.Equal(30, day.TotalTokens);
        Assert.Equal(0, day.CostUSD);
    }
}
