using AIUsage.Core.Models;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

public class OpenCodeProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private const string QuotaResponse = """
        {
          "product": "go",
          "access": {
            "startsAt": "2026-10-21T12:22:03Z",
            "endsAt": "2026-11-21T12:22:03Z",
            "meters": {
              "fiveHour": {
                "startsAt": "2026-10-22T09:18:01Z",
                "resetsAt": "2026-10-22T14:18:01Z",
                "limitMicroCents": 1200000000,
                "usedMicroCents": 36459826
              },
              "week": {
                "startsAt": "2026-10-19T00:00:00Z",
                "resetsAt": "2026-10-26T00:00:00Z",
                "limitMicroCents": 3000000000,
                "usedMicroCents": 537179900
              },
              "month": {
                "resetsAt": "2026-11-21T12:22:03Z",
                "limitMicroCents": 6000000000,
                "usedMicroCents": 537179900
              }
            }
          }
        }
        """;

    private static OpenCodeCredential Credential(DateTimeOffset? expiresAt = null) =>
        new(OpenCodeCredentialKind.OAuth, "console-token", "workspace", expiresAt, OpenCodeConsoleOrigin.Canonical);

    private static (OpenCodeTestStore Store, OpenCodeProvider Provider, FakeHttpClient Http, OpenCodeTestDatabase Db)
        CreateProvider(string? authJson = null, TimeZoneInfo? timeZone = null, DateTimeOffset? now = null)
    {
        var clock = now ?? Now;
        var store = new OpenCodeTestStore();
        var db = store.Database();
        if (authJson is not null) store.WriteAuthJson(authJson);
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", store.Root);
        var auth = new OpenCodeAuthStore(
            environment: environment,
            homeDirectory: () => store.Root,
            databasePaths: () => File.Exists(db.Path) ? new List<string> { db.Path } : new List<string>(),
            now: () => clock);
        var scanner = new OpenCodeUsageScanner(
            new SqliteDataAccessor(),
            () => File.Exists(db.Path) ? new List<string> { db.Path } : new List<string>(),
            timeZone: timeZone ?? TimeZoneInfo.Utc);
        var http = new FakeHttpClient();
        var provider = new OpenCodeProvider(
            auth, scanner, () => clock, new OpenCodeGoStatusClient(http));
        return (store, provider, http, db);
    }

    private static void InsertLocalUsage(OpenCodeTestDatabase db)
    {
        db.CreateLegacySchema();
        db.InsertLegacy("local", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: 0.75, totalTokens: 1200);
    }

    [Fact]
    public async Task AuthoritativeQuota_IsShownEvenWithNoLocalMessageTables()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("cred", "opencode",
                """{"type":"oauth","access":"console-token","expires":1893456000000,"metadata":{"orgID":"workspace"}}""",
                Now.ToUnixTimeMilliseconds());
            http.Enqueue(HttpResponseFixture.Json(QuotaResponse));

            var snapshot = await provider.RefreshAsync();

            var session = Assert.IsType<MetricLine.Progress>(snapshot.Line("Session"));
            var weekly = Assert.IsType<MetricLine.Progress>(snapshot.Line("Weekly"));
            var monthly = Assert.IsType<MetricLine.Progress>(snapshot.Line("Monthly"));
            Assert.Equal(12.0, session.Limit, 6);
            Assert.Equal(30.0, weekly.Limit, 6);
            Assert.Equal(60.0, monthly.Limit, 6);
            Assert.Equal(0.36459826, session.Used, 8);
            Assert.Equal("Go", snapshot.Plan);
            Assert.DoesNotContain(snapshot.Lines, l => l is MetricLine.Badge);
            Assert.Equal(OpenCodeQuotaCompatibility.CurrentDataVersion, snapshot.DataVersion);
        }
    }

    [Fact]
    public async Task ExpiredOAuth_ShowsReconnectAndKeepsLocalUsage()
    {
        var (store, provider, _, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            db.InsertCredential("cred", "opencode",
                """{"type":"oauth","access":"old","expires":1000,"metadata":{"orgID":"workspace"}}""",
                Now.ToUnixTimeMilliseconds());

            var snapshot = await provider.RefreshAsync();

            var badge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("Reconnect", badge.BadgeText);
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
            Assert.DoesNotContain(snapshot.Lines, l => l is MetricLine.Progress);
        }
    }

    [Fact]
    public async Task AuthRejection_ShowsFriendlyBadgeAndKeepsLocalUsage()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            db.InsertCredential("cred", "opencode",
                """{"type":"oauth","access":"console-token","expires":1893456000000,"metadata":{"orgID":"workspace"}}""",
                Now.ToUnixTimeMilliseconds());
            http.Enqueue(HttpResponseFixture.Json("{}", 401));

            var snapshot = await provider.RefreshAsync();

            var badge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("Reconnect", badge.BadgeText);
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
            Assert.DoesNotContain(snapshot.Lines, l => l.IsError);
        }
    }

    [Fact]
    public async Task V1GoKeyOnly_ShowsQuotaUnavailableWithoutGuessingCaps()
    {
        var (store, provider, http, db) = CreateProvider("""{"opencode-go":{"type":"api","key":"sk-legacy"}}""");
        using (store)
        {
            InsertLocalUsage(db);

            var snapshot = await provider.RefreshAsync();

            var badge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("Unavailable", badge.BadgeText);
            Assert.DoesNotContain(snapshot.Lines, l => l is MetricLine.Progress);
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
            Assert.Equal("Go", snapshot.Plan);
            Assert.Empty(http.Requests);
        }
    }

    [Fact]
    public async Task NoActiveGo_ShowsNoFabricatedMetersOrPlanFromStaleHistory()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            db.InsertCredential("cred", "opencode",
                """{"type":"oauth","access":"console-token","metadata":{"orgID":"workspace"}}""",
                Now.ToUnixTimeMilliseconds());
            http.Enqueue(HttpResponseFixture.Json("""{"product":"go","access":null}"""));

            var snapshot = await provider.RefreshAsync();

            var badge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("No Active Go", badge.BadgeText);
            Assert.DoesNotContain(snapshot.Lines, l => l is MetricLine.Progress);
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
            Assert.Null(snapshot.Plan);
        }
    }

    [Fact]
    public async Task ExplicitNullSubscription_AlsoClearsPlanFromStaleHistory()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            db.InsertCredential("cred", "opencode",
                """{"type":"oauth","access":"console-token","metadata":{"orgID":"workspace"}}""",
                Now.ToUnixTimeMilliseconds());
            http.Enqueue(HttpResponseFixture.Json("null"));

            var snapshot = await provider.RefreshAsync();

            var badge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("No Active Go", badge.BadgeText);
            Assert.Null(snapshot.Plan);
        }
    }

    [Fact]
    public async Task LocalScannerFailure_StillShowsServerQuotaWithLocalUsageBadge()
    {
        using var store = new OpenCodeTestStore();
        var credentialDb = store.Database();
        credentialDb.CreateCredentialSchema();
        credentialDb.InsertCredential("cred", "opencode",
            """{"type":"oauth","access":"console-token","expires":1893456000000,"metadata":{"orgID":"workspace"}}""",
            Now.ToUnixTimeMilliseconds());
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", store.Root);
        var auth = new OpenCodeAuthStore(
            environment: environment,
            homeDirectory: () => store.Root,
            databasePaths: () => new List<string> { credentialDb.Path },
            now: () => Now);
        var scanner = new OpenCodeUsageScanner(
            new ThrowingSqlite(),
            () => new List<string> { "broken.db" },
            timeZone: TimeZoneInfo.Utc);
        var http = new FakeHttpClient();
        http.Enqueue(HttpResponseFixture.Json(QuotaResponse));
        var provider = new OpenCodeProvider(auth, scanner, () => Now, new OpenCodeGoStatusClient(http));

        var snapshot = await provider.RefreshAsync();

        var session = Assert.IsType<MetricLine.Progress>(snapshot.Line("Session"));
        Assert.Equal(12.0, session.Limit, 6);
        var localBadge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Local Usage"));
        Assert.Equal("Unavailable", localBadge.BadgeText);
        Assert.DoesNotContain(snapshot.Lines, l => l.Label == "Last 30 Days");
        Assert.Equal("Go", snapshot.Plan);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task CallerCancellation_IsNotSwallowedAsCredentialFailure()
    {
        var (store, provider, _, _) = CreateProvider();
        using (store)
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RefreshAsync(cts.Token));
        }
    }

    [Fact]
    public async Task LocalCalendarNow_DrivesTodayAndYesterdayAcrossMidnight()
    {
        var plusSeven = TimeZoneInfo.CreateCustomTimeZone("UTC+07", TimeSpan.FromHours(7), "UTC+07", "UTC+07");
        var localNow = new DateTimeOffset(2026, 10, 22, 17, 30, 0, TimeSpan.Zero);
        var (store, provider, _, db) = CreateProvider(timeZone: plusSeven, now: localNow);
        using (store)
        {
            db.CreateV2Schema();
            db.InsertV2("after-local-midnight", localNow.AddMinutes(-15).ToUnixTimeMilliseconds(), cost: 1.0, input: 100);
            db.InsertV2("before-local-midnight", localNow.AddMinutes(-45).ToUnixTimeMilliseconds(), cost: 0.5, input: 200);

            var snapshot = await provider.RefreshAsync();

            var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
            var yesterday = Assert.IsType<MetricLine.Values>(snapshot.Line("Yesterday"));
            Assert.Equal(100, Assert.Single(today.ValuesList, v => v.Kind == MetricKind.Count).Number);
            Assert.Equal(200, Assert.Single(yesterday.ValuesList, v => v.Kind == MetricKind.Count).Number);
        }
    }

    private sealed class ThrowingSqlite : ISqliteAccessing
    {
        public string? QueryValue(string path, string sql) => throw new InvalidOperationException("unreadable");
        public void Execute(string path, string sql) => throw new NotSupportedException();
    }

    [Fact]
    public async Task NoLocalDatabaseAndNoCredential_ReturnsNotLoggedIn()
    {
        var (store, provider, _, _) = CreateProvider();
        using (store)
        {
            var snapshot = await provider.RefreshAsync();

            Assert.True(snapshot.Lines.Single().IsError);
            // Fatal error snapshots are never persisted as current quota data.
            Assert.Null(snapshot.DataVersion);
        }
    }

    [Fact]
    public async Task HasLocalCredentialsAsync_ReadsV2CredentialWithoutNetwork()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("cred", "opencode",
                """{"type":"oauth","access":"console-token","metadata":{"orgID":"workspace"}}""",
                Now.ToUnixTimeMilliseconds());

            Assert.True(await provider.HasLocalCredentialsAsync());
            Assert.Empty(http.Requests);
        }
    }

    [Fact]
    public async Task MissingAuthWithUsableLogs_KeepsLocalUsageAndReportsQuotaUnavailable()
    {
        var (store, provider, _, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);

            var snapshot = await provider.RefreshAsync();

            var badge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("Unavailable", badge.BadgeText);
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
            Assert.DoesNotContain(snapshot.Lines, l => l is MetricLine.Progress);
        }
    }

    [Fact]
    public async Task UnsupportedSchema_IsSurfacedNotTreatedAsZero()
    {
        var (store, provider, _, db) = CreateProvider("""{"opencode-go":{"type":"api","key":"sk-legacy"}}""");
        using (store)
        {
            db.CreateUnrelatedSchema();

            var snapshot = await provider.RefreshAsync();

            var badge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Local Usage"));
            Assert.Equal("Unsupported Schema", badge.BadgeText);
        }
    }

    [Fact]
    public async Task MultiGigatokenLocalUsage_PreservesServerQuotaAndComputesExact64BitTotals()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("cred", "opencode",
                """{"type":"oauth","access":"console-token","expires":1893456000000,"metadata":{"orgID":"workspace"}}""",
                Now.ToUnixTimeMilliseconds());
            db.CreateV2Schema();
            db.InsertV2("today-1", Now.AddHours(-2).ToUnixTimeMilliseconds(), cost: 5.0, input: 1_500_000_000L);
            db.InsertV2("today-2", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: 5.0, output: 1_500_000_000L);
            db.InsertV2("yesterday-1", Now.AddDays(-1).AddHours(-1).ToUnixTimeMilliseconds(), cost: 5.0, input: 1_500_000_000L);
            http.Enqueue(HttpResponseFixture.Json(QuotaResponse));

            var snapshot = await provider.RefreshAsync();

            var session = Assert.IsType<MetricLine.Progress>(snapshot.Line("Session"));
            Assert.Equal(12.0, session.Limit, 6);
            var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
            var last30 = Assert.IsType<MetricLine.Values>(snapshot.Line("Last 30 Days"));
            Assert.Equal(3_000_000_000d, today.ValuesList.Single(v => v.Kind == MetricKind.Count).Number);
            Assert.Equal(4_500_000_000d, last30.ValuesList.Single(v => v.Kind == MetricKind.Count).Number);
            Assert.Equal(4_500_000_000L, last30.ModelBreakdown!.TotalTokens);
            Assert.Equal(4_500_000_000L, snapshot.UsageHistory!.Series.Daily.Sum(d => d.TotalTokens));
        }
    }

    [Fact]
    public async Task MixedGoodAndUnreadableDatabases_MakesNoNetworkCallAndKeepsReadableLocalUsageWithVisibleBadges()
    {
        using var store = new OpenCodeTestStore();
        var readableDb = store.Database("opencode.db");
        readableDb.CreateCredentialSchema();
        readableDb.InsertCredential("older-valid", "opencode",
            """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
            Now.AddMinutes(-10).ToUnixTimeMilliseconds());
        InsertLocalUsage(readableDb);

        var paths = new List<string> { readableDb.Path, Path.Combine(store.Root, "opencode-next.db") };
        var sqlite = new NextDbFailingSqlite();
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", store.Root);
        var auth = new OpenCodeAuthStore(
            environment: environment,
            homeDirectory: () => store.Root,
            sqlite: sqlite,
            databasePaths: () => paths,
            now: () => Now);
        var scanner = new OpenCodeUsageScanner(
            sqlite,
            () => paths,
            timeZone: TimeZoneInfo.Utc);
        var http = new FakeHttpClient();
        var provider = new OpenCodeProvider(auth, scanner, () => Now, new OpenCodeGoStatusClient(http));

        var snapshot = await provider.RefreshAsync();

        Assert.Empty(http.Requests);
        Assert.DoesNotContain(snapshot.Lines, l => l is MetricLine.Progress);
        var quotaBadge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
        Assert.Equal("Unavailable", quotaBadge.BadgeText);
        var localBadge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Local Usage"));
        Assert.Equal("Partial Data", localBadge.BadgeText);
        Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
    }

    private sealed class NextDbFailingSqlite : ISqliteAccessing
    {
        private readonly SqliteDataAccessor _inner = new();

        public string? QueryValue(string path, string sql) =>
            path.EndsWith("opencode-next.db", StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("database is locked")
                : _inner.QueryValue(path, sql);

        public void Execute(string path, string sql) => _inner.Execute(path, sql);
    }
}
