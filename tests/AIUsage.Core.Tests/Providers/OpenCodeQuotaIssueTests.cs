using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;
using System.Text.Json;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

/// <summary>
/// Coverage for the nonfatal limits-error metadata OpenCode attaches when server quota is
/// unavailable but local usage is still readable. It must not become a fatal refresh error and must
/// not appear for a legitimate "No Active Go" answer or a Zen-only install.
/// </summary>
public class OpenCodeQuotaIssueTests
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
        CreateProvider(string? authJson = null)
    {
        var store = new OpenCodeTestStore();
        var db = store.Database();
        if (authJson is not null) store.WriteAuthJson(authJson);
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

    private static void InsertLocalUsage(OpenCodeTestDatabase db, string providerId = "opencode-go")
    {
        db.CreateLegacySchema();
        db.InsertLegacy("local", Now.AddHours(-1).ToUnixTimeMilliseconds(),
            providerId: providerId, cost: 0.75, totalTokens: 1200);
    }

    private static void InsertUsableCredential(OpenCodeTestDatabase db) =>
        db.InsertCredential("cred", "opencode",
            """{"type":"oauth","access":"console-token","expires":1893456000000,"metadata":{"orgID":"workspace"}}""",
            Now.ToUnixTimeMilliseconds());

    private static void InsertExpiredCredential(OpenCodeTestDatabase db) =>
        db.InsertCredential("cred", "opencode",
            """{"type":"oauth","access":"console-token","expires":1000,"metadata":{"orgID":"workspace"}}""",
            Now.ToUnixTimeMilliseconds());

    [Fact]
    public async Task AuthRejectedWhileLogsReadable_AttachesLimitsErrorAndKeepsLocalUsageAndQuotaBadge()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            InsertUsableCredential(db);
            http.Enqueue(HttpResponseFixture.Json("{}", 401));

            var snapshot = await provider.RefreshAsync();

            Assert.False(string.IsNullOrWhiteSpace(snapshot.LimitsError));
            Assert.Contains("rejected the saved sign-in", snapshot.LimitsError, StringComparison.Ordinal);
            Assert.DoesNotContain("console-token", snapshot.LimitsError, StringComparison.Ordinal);
            Assert.DoesNotContain(snapshot.Lines, l => l.IsError);

            var quotaBadge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("Reconnect", quotaBadge.BadgeText);
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
            Assert.Equal(OpenCodeQuotaCompatibility.CurrentDataVersion, snapshot.DataVersion);
        }
    }

    [Fact]
    public async Task NetworkFailureWhileLogsReadable_AttachesLimitsError()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            InsertUsableCredential(db);
            http.Fault = new HttpRequestException("network down");

            var snapshot = await provider.RefreshAsync();

            Assert.NotNull(snapshot.LimitsError);
            Assert.Contains("Couldn't reach OpenCode Console", snapshot.LimitsError, StringComparison.Ordinal);
            var quotaBadge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("Unavailable", quotaBadge.BadgeText);
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
        }
    }

    [Fact]
    public async Task ExpiredCredentialWhileLogsReadable_AttachesLimitsError()
    {
        var (store, provider, _, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            InsertExpiredCredential(db);

            var snapshot = await provider.RefreshAsync();

            Assert.NotNull(snapshot.LimitsError);
            Assert.Contains("sign-in expired", snapshot.LimitsError, StringComparison.Ordinal);
            var quotaBadge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("Reconnect", quotaBadge.BadgeText);
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
        }
    }

    [Fact]
    public async Task NoActiveGo_IsAValidAnswerAndDoesNotAttachLimitsError()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            InsertUsableCredential(db);
            http.Enqueue(HttpResponseFixture.Json("""{"product":"go","access":null}"""));

            var snapshot = await provider.RefreshAsync();

            Assert.Null(snapshot.LimitsError);
            var quotaBadge = Assert.IsType<MetricLine.Badge>(snapshot.Line("Go Quota"));
            Assert.Equal("No Active Go", quotaBadge.BadgeText);
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
            Assert.Equal(OpenCodeQuotaCompatibility.CurrentDataVersion, snapshot.DataVersion);
        }
    }

    [Fact]
    public async Task ZenOnlyUsageWithoutAGoCredential_StaysNormalWithoutLimitsError()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db, providerId: "opencode");

            var snapshot = await provider.RefreshAsync();

            Assert.Null(snapshot.LimitsError);
            Assert.Null(snapshot.Line("Go Quota"));
            Assert.Single(snapshot.Lines, l => l.Label == "Last 30 Days");
            Assert.Empty(http.Requests);
            Assert.Equal(OpenCodeQuotaCompatibility.CurrentDataVersion, snapshot.DataVersion);
        }
    }

    [Fact]
    public async Task SuccessfulQuotaAnswerAfterFailure_ClearsLimitsError()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            InsertUsableCredential(db);
            http.Enqueue(HttpResponseFixture.Json("{}", 401));
            http.Enqueue(HttpResponseFixture.Json(QuotaResponse));

            var failed = await provider.RefreshAsync();
            Assert.NotNull(failed.LimitsError);

            var recovered = await provider.RefreshAsync();

            Assert.Null(recovered.LimitsError);
            Assert.IsType<MetricLine.Progress>(recovered.Line("Session"));
            Assert.Single(recovered.Lines, l => l.Label == "Last 30 Days");
            Assert.Equal(OpenCodeQuotaCompatibility.CurrentDataVersion, recovered.DataVersion);
        }
    }

    [Fact]
    public async Task PartialSnapshotWithLimitsError_StoresNormallyAndDoesNotBecomeAFatalCardStatus()
    {
        var (store, provider, http, db) = CreateProvider();
        using (store)
        {
            InsertLocalUsage(db);
            db.CreateCredentialSchema();
            InsertUsableCredential(db);
            http.Enqueue(HttpResponseFixture.Json("{}", 401));

            var registry = new WidgetRegistry(new List<Provider> { provider.Provider }, provider.WidgetDescriptors);
            var cache = new ProviderSnapshotCache(new InMemorySettingsStore(), now: () => Now);
            var dataStore = new WidgetDataStore(
                registry, new List<IProviderRuntime> { provider }, cache, now: () => Now);

            var outcome = await dataStore.RefreshAsync("opencode", force: true);

            Assert.Equal(RefreshOutcome.Refreshed, outcome);
            Assert.Empty(dataStore.ProviderErrors);
            Assert.True(dataStore.Snapshots.ContainsKey("opencode"));
            Assert.NotNull(dataStore.Snapshots["opencode"].LimitsError);
            Assert.True(dataStore.Data(provider.WidgetDescriptors.Single(d => d.Id == "opencode.today")).HasData);

            // A current partial-quota-error snapshot is cacheable (stamped), unlike legacy caches.
            var cached = cache.Snapshot("opencode");
            Assert.NotNull(cached);
            Assert.Equal(OpenCodeQuotaCompatibility.CurrentDataVersion, cached!.DataVersion);
            Assert.Equal(dataStore.Snapshots["opencode"].LimitsError, cached.LimitsError);

            var badges = dataStore.StatusBadges("opencode");
            Assert.Contains(badges, b => b.Title == "Go Quota" && b.Headline == "Reconnect");
            Assert.DoesNotContain(badges, b => b.Headline == "Refresh Failed");

            // The failed quota request must not fabricate limit resources; the issue is carried by
            // the existing errors envelope instead.
            var snapshot = dataStore.Snapshots["opencode"];
            var state = new LocalUsageApi.State(
                EnabledOrderedIds: new List<string> { "opencode" },
                KnownIds: new HashSet<string> { "opencode" },
                Snapshots: dataStore.Snapshots,
                LimitDescriptors: registry.LimitDescriptorsByProvider,
                Errors: dataStore.ProviderErrors);
            var json = JsonDocument.Parse(LocalLimitsApi.Encode(new List<string> { "opencode" }, state)).RootElement;
            Assert.Empty(json.GetProperty("providers").GetProperty("opencode")
                .GetProperty("resources").EnumerateObject());
            var errors = json.GetProperty("errors");
            Assert.Equal(1, errors.GetArrayLength());
            Assert.Equal(snapshot.LimitsError, errors[0].GetProperty("message").GetString());
        }
    }
}
