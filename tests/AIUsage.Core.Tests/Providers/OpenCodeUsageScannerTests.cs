using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

public class OpenCodeUsageScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static OpenCodeUsageScanner Scanner(params string[] databases) =>
        ScannerIn(TimeZoneInfo.Utc, databases);

    private static OpenCodeUsageScanner ScannerIn(TimeZoneInfo timeZone, params string[] databases) =>
        new(new SqliteDataAccessor(), () => databases.ToList(), timeZone: timeZone);

    private static double TotalCost(OpenCodeUsageScan scan) =>
        scan.LogScan.Series.Daily.Sum(d => d.CostUSD ?? 0);

    private static long TotalTokens(OpenCodeUsageScan scan) =>
        scan.LogScan.Series.Daily.Sum(d => d.TotalTokens);

    [Fact]
    public async Task V1Only_ReadsAssistantRowsForHostedProvidersOnly()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateLegacySchema();
        db.InsertLegacy("go-1", Now.AddHours(-1).ToUnixTimeMilliseconds(), "opencode-go", cost: 1.25, totalTokens: 1000);
        db.InsertLegacy("zen-1", Now.AddHours(-2).ToUnixTimeMilliseconds(), "opencode", cost: 0.5, totalTokens: 500);
        db.InsertLegacy("other", Now.AddHours(-3).ToUnixTimeMilliseconds(), "some-other-provider", modelId: "other-model", cost: 99, totalTokens: 9000);
        db.InsertLegacy("user", Now.AddHours(-4).ToUnixTimeMilliseconds(), "opencode-go", role: "user");
        db.InsertLegacy("inflight", Now.AddHours(-5).ToUnixTimeMilliseconds(), "opencode-go", cost: null, totalTokens: null);

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.True(scan!.Report.HasLegacySchema);
        Assert.False(scan.Report.HasV2Schema);
        Assert.True(scan.Report.HasHostedUsage);
        Assert.True(scan.Report.HasGoUsage);
        Assert.Equal(1.75, TotalCost(scan), 4);
        Assert.Equal(1500, TotalTokens(scan));
        Assert.Contains(
            scan.LogScan.ModelUsage!.Daily.SelectMany(d => d.Models),
            m => m.Model == "claude-sonnet-4");
        Assert.DoesNotContain(
            scan.LogScan.ModelUsage.Daily.SelectMany(d => d.Models),
            m => m.Model == "other-model");
    }

    [Fact]
    public async Task V2Only_SumsInputOutputReasoningAndCacheTokens()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2("v2-1", Now.AddHours(-1).ToUnixTimeMilliseconds(),
            cost: 2.0, input: 10, output: 20, reasoning: 30, cacheRead: 40, cacheWrite: 50);
        db.InsertV2("v2-2", Now.AddHours(-2).ToUnixTimeMilliseconds(),
            providerId: "opencode", cost: 0.1, input: 1, output: 2, cacheRead: 2);
        db.InsertV2("user", Now.AddHours(-3).ToUnixTimeMilliseconds(), type: "user", cost: 99, input: 9999);

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.True(scan!.Report.HasV2Schema);
        Assert.Equal(155, TotalTokens(scan));
        Assert.Equal(2.1, TotalCost(scan), 4);
    }

    [Fact]
    public async Task V2Only_MissingCostKeepsTokensButDoesNotInventZeroDollars()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2("inflight", Now.AddMinutes(-5).ToUnixTimeMilliseconds(), cost: null, input: 100, output: 20);

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        var day = Assert.Single(scan!.LogScan.Series.Daily);
        Assert.Equal(120, day.TotalTokens);
        Assert.Null(day.CostUSD);
    }

    [Fact]
    public async Task MixedMigratedIds_V2WinsAndRowsAreNotSummedTwice()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateLegacySchema();
        db.CreateV2Schema();
        db.InsertLegacy("migrated", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: 9.0, totalTokens: 900);
        db.InsertV2("migrated", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: 1.0, input: 100);
        db.InsertLegacy("legacy-only", Now.AddHours(-2).ToUnixTimeMilliseconds(), cost: 0.5, totalTokens: 10);
        db.InsertV2("v2-only", Now.AddHours(-3).ToUnixTimeMilliseconds(), cost: 0.25, input: 5);

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(1.75, TotalCost(scan!), 4);
        Assert.Equal(115, TotalTokens(scan!));
    }

    [Fact]
    public async Task SameMessageIdAcrossDatabases_IsCountedOnce()
    {
        using var store = new OpenCodeTestStore();
        var first = store.Database("opencode.db");
        var second = store.Database("opencode-next.db");
        first.CreateLegacySchema();
        first.InsertLegacy("shared", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: 9.0, totalTokens: 900);
        first.InsertLegacy("legacy-only", Now.AddHours(-2).ToUnixTimeMilliseconds(), cost: 0.5, totalTokens: 10);
        second.CreateV2Schema();
        second.InsertV2("shared", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: 1.0, input: 100);
        second.InsertV2("v2-only", Now.AddHours(-3).ToUnixTimeMilliseconds(), cost: 0.25, input: 5);

        var scan = await Scanner(first.Path, second.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(1.75, TotalCost(scan!), 4);
        Assert.Equal(115, TotalTokens(scan!));
        Assert.Equal(2, scan!.Report.DatabaseCount);
    }

    [Fact]
    public async Task UnknownSchema_IsReportedInsteadOfTreatedAsZero()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateUnrelatedSchema();

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(1, scan!.Report.UnsupportedDatabaseCount);
        Assert.False(scan.Report.HasLegacySchema);
        Assert.False(scan.Report.HasV2Schema);
        Assert.Empty(scan.LogScan.Series.Daily);
    }

    [Fact]
    public async Task RecognizedButEmptySchema_IsAValidZero()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.Execute("CREATE TABLE session (id TEXT PRIMARY KEY);");
        db.Execute("CREATE TABLE credential (id TEXT PRIMARY KEY);");

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(0, scan!.Report.UnsupportedDatabaseCount);
        Assert.Empty(scan.LogScan.Series.Daily);
    }

    [Fact]
    public async Task ReadFailure_ThrowsDatabaseUnreadable()
    {
        using var store = new OpenCodeTestStore();
        var scanner = new OpenCodeUsageScanner(new ThrowingSqlite(), () => new List<string> { "broken.db" });

        var error = await Assert.ThrowsAsync<OpenCodeUsageError>(() => scanner.ScanAsync(Now));

        Assert.Equal(OpenCodeUsageErrorKind.DatabaseUnreadable, error.Kind);
    }

    [Fact]
    public void HostedProbe_DetectsV2RowsWithoutReadingMessages()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2("v2", Now.ToUnixTimeMilliseconds(), input: 1);
        var scanner = Scanner(db.Path);

        Assert.True(scanner.HasHostedUsage());
    }

    [Fact]
    public async Task ZeroCostRow_IsKnownZeroRatherThanMissing()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2("zero", Now.AddMinutes(-5).ToUnixTimeMilliseconds(), cost: 0, input: 10);

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        var day = Assert.Single(scan!.LogScan.Series.Daily);
        Assert.Equal(10, day.TotalTokens);
        Assert.Equal(0, day.CostUSD);
    }

    [Fact]
    public async Task V2TotalOnlyRow_UsesTotalFallback()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2Raw(
            "total-only",
            Now.AddMinutes(-5).ToUnixTimeMilliseconds(),
            "assistant",
            """{"model":{"providerID":"opencode-go","id":"m"},"cost":0.25,"tokens":{"total":50}}""");

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(50, TotalTokens(scan!));
        Assert.Equal(0.25, TotalCost(scan!), 4);
    }

    [Fact]
    public async Task V2WithoutTypeColumn_FallsBackToRoleInData()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.Execute("CREATE TABLE session_message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT);");
        db.Execute(
            "INSERT INTO session_message (id, session_id, time_created, time_updated, data) VALUES ('no-type', 's', @time, @time, @data);",
            ("@time", Now.AddMinutes(-5).ToUnixTimeMilliseconds()),
            ("@data", """{"role":"assistant","model":{"providerID":"opencode-go","id":"m"},"cost":0.5,"tokens":{"input":7}}"""));

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(7, TotalTokens(scan!));
        Assert.Equal(0.5, TotalCost(scan!), 4);
    }

    [Fact]
    public async Task VersionHint_IsReportedButNeverGatesSchemaReading()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateLegacySchema();
        db.InsertLegacy("v1", Now.AddMinutes(-5).ToUnixTimeMilliseconds(), totalTokens: 42);
        var scanner = new OpenCodeUsageScanner(
            new SqliteDataAccessor(),
            () => new List<string> { db.Path },
            versionProbe: new OpenCodeVersionProbe(_ => Task.FromResult<string?>("2.1.2")));

        var scan = await scanner.ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal("2.1.2", scan!.Report.VersionHint);
        Assert.Equal(42, TotalTokens(scan));
    }

    [Fact]
    public async Task MalformedVersion_DoesNotBreakSchemaReading()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateLegacySchema();
        db.InsertLegacy("v1", Now.AddMinutes(-5).ToUnixTimeMilliseconds(), totalTokens: 42);
        var scanner = new OpenCodeUsageScanner(
            new SqliteDataAccessor(),
            () => new List<string> { db.Path },
            versionProbe: new OpenCodeVersionProbe(_ => Task.FromResult<string?>("not-a-version")));

        var scan = await scanner.ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Null(scan!.Report.VersionHint);
        Assert.Equal(42, TotalTokens(scan));
    }

    [Fact]
    public async Task UtcPlusSevenNearMidnight_UsesLocalCalendarDays()
    {
        var plusSeven = TimeZoneInfo.CreateCustomTimeZone("UTC+07", TimeSpan.FromHours(7), "UTC+07", "UTC+07");
        var now = new DateTimeOffset(2026, 10, 22, 17, 30, 0, TimeSpan.Zero);
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        // 17:15Z is already 00:15 on Oct 23 locally; 16:45Z is 23:45 on Oct 22.
        db.InsertV2("after-local-midnight", now.AddMinutes(-15).ToUnixTimeMilliseconds(), input: 100);
        db.InsertV2("before-local-midnight", now.AddMinutes(-45).ToUnixTimeMilliseconds(), input: 200);

        var scan = await ScannerIn(plusSeven, db.Path).ScanAsync(now);

        Assert.NotNull(scan);
        var days = scan!.LogScan.Series.Daily.ToDictionary(d => d.Date, d => d.TotalTokens);
        Assert.Equal(100, days["2026-10-23"]);
        Assert.Equal(200, days["2026-10-22"]);
        Assert.Equal("2026-10-23", DailyUsageAccumulator.DayKey(scan.CalendarNow));
        Assert.Equal(TimeSpan.FromHours(7), scan.CalendarNow.Offset);
    }

    [Fact]
    public async Task DstFallBack_KeepsBothSidesOfTransitionsOnTheSameLocalDay()
    {
        var eastern = FindEasternTimeZone();
        // 05:30Z is 01:30 EDT, before the 2026-11-01 fall-back at 02:00 local.
        var now = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2("before-fallback", new DateTimeOffset(2026, 11, 1, 4, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), input: 100);
        db.InsertV2("after-fallback", new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), input: 200);

        var scan = await ScannerIn(eastern, db.Path).ScanAsync(now);

        Assert.NotNull(scan);
        var day = Assert.Single(scan!.LogScan.Series.Daily);
        Assert.Equal("2026-11-01", day.Date);
        Assert.Equal(300, day.TotalTokens);
        Assert.Equal("2026-11-01", DailyUsageAccumulator.DayKey(scan.CalendarNow));
        Assert.Equal(TimeSpan.FromHours(-4), scan.CalendarNow.Offset);
    }

    [Fact]
    public async Task DstSpringForward_KeepsBothSidesOfTheGapOnTheSameLocalDay()
    {
        var eastern = FindEasternTimeZone();
        // 07:30Z is 03:30 EDT after the 2026-03-08 transition at 02:00 local.
        var now = new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero);
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2("before-gap", new DateTimeOffset(2026, 3, 8, 6, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), input: 100);
        db.InsertV2("after-gap", new DateTimeOffset(2026, 3, 8, 7, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), input: 200);

        var scan = await ScannerIn(eastern, db.Path).ScanAsync(now);

        Assert.NotNull(scan);
        var day = Assert.Single(scan!.LogScan.Series.Daily);
        Assert.Equal("2026-03-08", day.Date);
        Assert.Equal(300, day.TotalTokens);
        Assert.Equal("2026-03-08", DailyUsageAccumulator.DayKey(scan.CalendarNow));
        Assert.Equal(TimeSpan.FromHours(-4), scan.CalendarNow.Offset);
    }

    [Fact]
    public async Task MalformedQueryPayload_IsReportedAsPartialInsteadOfSilentZero()
    {
        var sqlite = new ScriptedSqlite((sql, _) =>
            sql.Contains("sqlite_master", StringComparison.Ordinal) ? "message"
            : sql.Contains("FROM message", StringComparison.Ordinal) ? "{not json"
            : null);
        var scanner = new OpenCodeUsageScanner(
            sqlite, () => new List<string> { "broken.db" }, timeZone: TimeZoneInfo.Utc);

        var scan = await scanner.ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(1, scan!.Report.FailedDatabaseCount);
        Assert.Empty(scan.LogScan.Series.Daily);
    }

    [Fact]
    public void HostedProbe_IgnoresMalformedV2Payloads()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2Raw("bad", Now.ToUnixTimeMilliseconds(), "assistant", "{not json");

        Assert.False(Scanner(db.Path).HasHostedUsage());
    }

    [Fact]
    public async Task V2ColumnProbeFailure_StillReadsTheDocumentedShape()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2("v2", Now.AddMinutes(-5).ToUnixTimeMilliseconds(), input: 7);
        var scanner = new OpenCodeUsageScanner(
            new ColumnProbeFailingSqlite(), () => new List<string> { db.Path }, timeZone: TimeZoneInfo.Utc);

        var scan = await scanner.ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(7, TotalTokens(scan!));
    }

    [Fact]
    public async Task PerRowAndSameDayTotals_AboveInt32Max_DoNotClampOrWrapNegative()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateLegacySchema();
        db.CreateV2Schema();
        db.InsertLegacy("legacy-big", Now.AddHours(-2).ToUnixTimeMilliseconds(),
            modelId: "claude-sonnet-4", cost: 4.5, totalTokens: 3_000_000_000L);
        db.InsertV2("v2-big-1", Now.AddHours(-1).ToUnixTimeMilliseconds(),
            modelId: "claude-opus-4", cost: 2.25, input: 1_500_000_000L);
        db.InsertV2("v2-big-2", Now.AddMinutes(-30).ToUnixTimeMilliseconds(),
            modelId: "claude-opus-4", cost: 2.25, output: 1_500_000_000L);

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        var day = Assert.Single(scan!.LogScan.Series.Daily);
        Assert.Equal(6_000_000_000L, day.TotalTokens);
        Assert.Equal(9.0, day.CostUSD!.Value, 4);

        var models = scan.LogScan.ModelUsage!.Daily.Single().Models.ToDictionary(m => m.Model, m => m.TotalTokens);
        Assert.Equal(3_000_000_000L, models["claude-sonnet-4"]);
        Assert.Equal(3_000_000_000L, models["claude-opus-4"]);
    }

    [Fact]
    public void SpendTileMapper_MultiDay64BitTotalsVariantsAndFolding_DoNotOverflow()
    {
        var todayKey = DailyUsageAccumulator.DayKey(Now);
        var yesterdayKey = DailyUsageAccumulator.DayKey(Now.AddDays(-1));
        var accumulator = new DailyUsageAccumulator();
        accumulator.Add(todayKey, 1_500_000_000L, 10.0, "Claude-Sonnet-4");
        accumulator.Add(todayKey, 1_600_000_000L, 12.0, "claude-sonnet-4");
        accumulator.Add(yesterdayKey, 1_500_000_000L, 11.0, "gpt-5");
        accumulator.Add(yesterdayKey, 2_500_000_000L, null, ModelUsageEntry.UnattributedModelName);

        var scan = accumulator.Build();
        var lines = new List<MetricLine>();
        SpendTileMapper.AppendTokenUsage(
            scan.Series, lines, Now, estimated: false,
            unknownModelsByDay: scan.UnknownModelsByDay,
            modelUsage: scan.ModelUsage,
            modelSourceNote: OpenCodeUsageMapper.QuotaSourceNote);

        var today = Assert.IsType<MetricLine.Values>(lines.Single(l => l.Label == "Today"));
        var last30 = Assert.IsType<MetricLine.Values>(lines.Single(l => l.Label == "Last 30 Days"));
        Assert.Equal(3_100_000_000d, today.ValuesList.Single(v => v.Kind == MetricKind.Count).Number);
        Assert.Equal(7_100_000_000d, last30.ValuesList.Single(v => v.Kind == MetricKind.Count).Number);
        Assert.Equal(7_100_000_000L, last30.ModelBreakdown!.TotalTokens);

        // Lowercase spelling has higher 64-bit token weight (1.6B vs 1.5B) and wins DisplayName;
        // Unattributed (2.5B) folds into Other without 32-bit overflow.
        var sonnet = last30.ModelBreakdown.Models.Single(m => m.Model == "claude-sonnet-4");
        Assert.Equal(3_100_000_000L, sonnet.TotalTokens);
        var other = last30.ModelBreakdown.Models.Single(m => m.Model == ModelUsageEntry.OtherModelName);
        Assert.Equal(2_500_000_000L, other.TotalTokens);
    }

    [Fact]
    public async Task MalformedOrExtremeTokenCounts_AreFlaggedAsPartialDataAtBoundary()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateV2Schema();
        db.InsertV2("good", Now.AddMinutes(-10).ToUnixTimeMilliseconds(), cost: 0.5, input: 2_500_000_000L);
        db.InsertV2Raw("negative", Now.AddMinutes(-5).ToUnixTimeMilliseconds(), "assistant",
            """{"model":{"providerID":"opencode-go","id":"m"},"cost":0.5,"tokens":{"input":-100}}""");
        db.InsertV2Raw("extreme", Now.AddMinutes(-4).ToUnixTimeMilliseconds(), "assistant",
            """{"model":{"providerID":"opencode-go","id":"m"},"cost":0.5,"tokens":{"input":1e30}}""");

        var scan = await Scanner(db.Path).ScanAsync(Now);

        Assert.NotNull(scan);
        Assert.Equal(1, scan!.Report.FailedDatabaseCount);
        Assert.Equal(2_500_000_000L, TotalTokens(scan));
    }

    private static TimeZoneInfo FindEasternTimeZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        throw new InvalidOperationException("The test host has no Eastern time zone.");
    }

    private sealed class ScriptedSqlite : ISqliteAccessing
    {
        private readonly Func<string, string, string?> _answer;
        public ScriptedSqlite(Func<string, string, string?> answer) => _answer = answer;
        public string? QueryValue(string path, string sql) => _answer(sql, path);
        public void Execute(string path, string sql) => throw new NotSupportedException();
    }

    private sealed class ColumnProbeFailingSqlite : ISqliteAccessing
    {
        private readonly SqliteDataAccessor _inner = new();
        public string? QueryValue(string path, string sql) =>
            sql.Contains("pragma_table_info", StringComparison.Ordinal)
                ? throw new InvalidOperationException("column probe unavailable")
                : _inner.QueryValue(path, sql);
        public void Execute(string path, string sql) => _inner.Execute(path, sql);
    }

    private sealed class ThrowingSqlite : ISqliteAccessing
    {
        public string? QueryValue(string path, string sql) => throw new InvalidOperationException("unreadable");
        public void Execute(string path, string sql) => throw new NotSupportedException();
    }
}
