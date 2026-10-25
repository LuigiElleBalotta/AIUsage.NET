using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

/// <summary>
/// Partial-cost coverage for OpenCode local spend: known dollar subtotals stay visible, but a mixed
/// history is labelled incomplete instead of presenting the subtotal as a complete total or guessing
/// a cost for messages that have none.
/// </summary>
public class OpenCodePartialCostTests
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

    private static (OpenCodeTestStore Store, OpenCodeProvider Provider, FakeHttpClient Http, OpenCodeTestDatabase Db)
        CreateProvider()
    {
        var store = new OpenCodeTestStore();
        var db = store.Database();
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", store.Root);
        var auth = new OpenCodeAuthStore(
            environment: environment,
            homeDirectory: () => store.Root,
            databasePaths: () => File.Exists(db.Path) ? new List<string> { db.Path } : new List<string>(),
            now: () => Now);
        var scanner = new OpenCodeUsageScanner(
            new SqliteDataAccessor(),
            () => File.Exists(db.Path) ? new List<string> { db.Path } : new List<string>(),
            timeZone: TimeZoneInfo.Utc);
        var http = new FakeHttpClient();
        var provider = new OpenCodeProvider(auth, scanner, () => Now, new OpenCodeGoStatusClient(http));
        return (store, provider, http, db);
    }

    private static void PrepareAuthoritativeQuota(OpenCodeTestDatabase db, FakeHttpClient http)
    {
        db.CreateCredentialSchema();
        db.InsertCredential("cred", "opencode",
            """{"type":"oauth","access":"console-token","expires":1893456000000,"metadata":{"orgID":"workspace"}}""",
            Now.ToUnixTimeMilliseconds());
        http.Enqueue(HttpResponseFixture.Json(QuotaResponse));
    }

    private static long Tokens(MetricLine.Values values) =>
        (long)values.ValuesList.Single(v => v.Kind == MetricKind.Count).Number;

    private static double? Dollars(MetricLine.Values values) =>
        values.ValuesList.SingleOrDefault(v => v.Kind == MetricKind.Dollars)?.Number;

    [Fact]
    public async Task MixedKnownZeroPositiveAndMissingCost_ShowsKnownSubtotalWithPartialCostBadge()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            PrepareAuthoritativeQuota(db, http);
            db.CreateV2Schema();
            db.InsertV2("known-zero", Now.AddHours(-3).ToUnixTimeMilliseconds(), cost: 0, input: 10);
            db.InsertV2("known-positive", Now.AddHours(-2).ToUnixTimeMilliseconds(), cost: 5.0, input: 15);
            db.InsertV2("missing", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: null, input: 20);

            var snapshot = await provider.RefreshAsync();

            var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
            var last30 = Assert.IsType<MetricLine.Values>(snapshot.Line("Last 30 Days"));
            Assert.Equal(5.0, Dollars(today));
            Assert.Equal(45, Tokens(today));
            Assert.Equal(5.0, Dollars(last30));
            Assert.Equal(45, Tokens(last30));

            var badge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Local Usage"));
            Assert.Equal("Partial Cost", badge.BadgeText);
            Assert.Contains("known subtotals", badge.Subtitle, StringComparison.Ordinal);
            Assert.DoesNotContain("estimated", badge.Subtitle, StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(today.ModelBreakdown);
            Assert.Contains("known subtotals", today.ModelBreakdown!.SourceNote, StringComparison.Ordinal);
            Assert.Equal(5.0, today.ModelBreakdown.TotalCostUSD);

            // The valid server quota is untouched by the local partial-cost note.
            var session = Assert.IsType<MetricLine.Progress>(snapshot.Line("Session"));
            Assert.Equal(12.0, session.Limit, 6);
        }
    }

    [Fact]
    public async Task MissingCostOnADifferentRenderedDay_StillMarksTheSubtotalIncomplete()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            PrepareAuthoritativeQuota(db, http);
            db.CreateV2Schema();
            db.InsertV2("yesterday", Now.AddDays(-1).ToUnixTimeMilliseconds(), cost: 2.0, input: 10);
            db.InsertV2("today-missing", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: null, input: 20);

            var snapshot = await provider.RefreshAsync();

            var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
            Assert.Null(Dollars(today));
            Assert.Equal(20, Tokens(today));
            var last30 = Assert.IsType<MetricLine.Values>(snapshot.Line("Last 30 Days"));
            Assert.Equal(2.0, Dollars(last30));
            Assert.Equal(30, Tokens(last30));
            Assert.Equal("Partial Cost", Assert.IsType<MetricLine.Badge>(snapshot.Line("Local Usage")).BadgeText);
        }
    }

    [Fact]
    public async Task AllMissingCost_StaysTokenOnlyWithoutAFakeZeroOrPartialBadge()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            PrepareAuthoritativeQuota(db, http);
            db.CreateV2Schema();
            db.InsertV2("missing", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: null, input: 20);

            var scan = await provider.UsageScanner.ScanAsync(Now);
            Assert.NotNull(scan);
            Assert.True(scan!.Report.HasMissingCost);

            var snapshot = await provider.RefreshAsync();

            var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
            Assert.Null(Dollars(today));
            Assert.Equal(20, Tokens(today));
            var last30 = Assert.IsType<MetricLine.Values>(snapshot.Line("Last 30 Days"));
            Assert.Null(Dollars(last30));
            Assert.Equal(20, Tokens(last30));
            Assert.Null(snapshot.Line("Local Usage"));
        }
    }

    [Fact]
    public async Task AllKnownCosts_IncludingAtrueZero_DoNotProduceAPartialCostBadge()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            PrepareAuthoritativeQuota(db, http);
            db.CreateV2Schema();
            db.InsertV2("known-zero", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: 0, input: 10);

            var snapshot = await provider.RefreshAsync();

            var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
            Assert.Equal(0.0, Dollars(today));
            Assert.Equal(10, Tokens(today));
            Assert.Null(snapshot.Line("Local Usage"));
        }
    }

    [Fact]
    public async Task MissingCostOnASuppressedDuplicate_DoesNotMarkTheWinnerIncomplete()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            PrepareAuthoritativeQuota(db, http);
            db.CreateLegacySchema();
            db.CreateV2Schema();
            db.InsertLegacy("duplicate", Now.AddHours(-2).ToUnixTimeMilliseconds(), cost: null, totalTokens: 20);
            db.InsertV2("duplicate", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: 1.0, input: 5);

            var snapshot = await provider.RefreshAsync();

            var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
            Assert.Equal(1.0, Dollars(today));
            Assert.Equal(5, Tokens(today));
            Assert.DoesNotContain(snapshot.Lines, l => l.Label == "Local Usage" && l is MetricLine.Badge);
        }
    }

    [Fact]
    public async Task MultiBillionTokenMixedHistory_Keeps64BitTotalsAndTheKnownSubtotal()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            PrepareAuthoritativeQuota(db, http);
            db.CreateV2Schema();
            db.InsertV2("known", Now.AddHours(-2).ToUnixTimeMilliseconds(), cost: 5.0, input: 1_500_000_000L);
            db.InsertV2("missing", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: null, output: 1_500_000_000L);

            var snapshot = await provider.RefreshAsync();

            var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
            Assert.Equal(5.0, Dollars(today));
            Assert.Equal(3_000_000_000L, Tokens(today));
            Assert.Equal(3_000_000_000L, today.ModelBreakdown!.TotalTokens);
            Assert.Equal("Partial Cost", Assert.IsType<MetricLine.Badge>(snapshot.Line("Local Usage")).BadgeText);
        }
    }

    [Fact]
    public async Task MissingCostOutsideTheRenderedWindow_DoesNotMarkTheSubtotalIncomplete()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            PrepareAuthoritativeQuota(db, http);
            db.CreateV2Schema();
            db.InsertV2("known", Now.AddHours(-1).ToUnixTimeMilliseconds(), cost: 1.0, input: 10);
            db.InsertV2("too-old", Now.AddDays(-31).ToUnixTimeMilliseconds(), cost: null, input: 20);

            var scan = await provider.UsageScanner.ScanAsync(Now);
            Assert.NotNull(scan);
            Assert.False(scan!.Report.HasMissingCost);

            var snapshot = await provider.RefreshAsync();

            var today = Assert.IsType<MetricLine.Values>(snapshot.Line("Today"));
            Assert.Equal(1.0, Dollars(today));
            Assert.Equal(10, Tokens(today));
            Assert.Null(snapshot.Line("Local Usage"));
        }
    }
}
