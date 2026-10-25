using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

public class OpenCodeAuthStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static (OpenCodeTestStore Store, OpenCodeAuthStore Auth, OpenCodeTestDatabase Db) Create()
    {
        var store = new OpenCodeTestStore();
        var db = store.Database();
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", store.Root);
        var auth = new OpenCodeAuthStore(
            environment: environment,
            homeDirectory: () => store.Root,
            databasePaths: () => new List<string> { db.Path },
            now: () => Now);
        return (store, auth, db);
    }

    private static OpenCodeAuthStore AuthFor(OpenCodeTestStore store, params OpenCodeTestDatabase[] databases)
    {
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", store.Root);
        return new OpenCodeAuthStore(
            environment: environment,
            homeDirectory: () => store.Root,
            databasePaths: () => databases.Select(d => d.Path).ToList(),
            now: () => Now);
    }

    [Fact]
    public void V1GoApiKey_IsPreserved()
    {
        var (store, auth, _) = Create();
        using (store)
        {
            store.WriteAuthJson("""{"opencode-go":{"type":"api","key":"sk-legacy-go"}}""");

            Assert.Equal("sk-legacy-go", auth.GoApiKey());
            Assert.True(auth.HasAnyLocalCredential());
        }
    }

    [Fact]
    public void V1ConsoleOAuth_IsReadWhenThereIsNoV2Credential()
    {
        var (store, auth, _) = Create();
        using (store)
        {
            store.WriteAuthJson("""
                {"opencode":{"type":"oauth","access":"v1-access","refresh":"v1-refresh","expires":1893456000000,"metadata":{"server":"https://console.opencode.ai","orgID":"v1-org"}}}
                """);

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.Usable, state.Status);
            Assert.Equal("v1-access", state.Credential!.Token);
            Assert.Equal("v1-org", state.Credential.OrgId);
            Assert.Equal(OpenCodeConsoleOrigin.Canonical, state.Credential.Origin);
        }
    }

    [Fact]
    public void V1ConsoleApiKey_IsSupported()
    {
        var (store, auth, _) = Create();
        using (store)
        {
            store.WriteAuthJson("""
                {"opencode":{"type":"api","key":"v1-console-key","metadata":{"orgID":"v1-org"}}}
                """);

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.Usable, state.Status);
            Assert.Equal(OpenCodeCredentialKind.ApiKey, state.Credential!.Kind);
            Assert.Equal("v1-console-key", state.Credential.Token);
            Assert.Equal("v1-org", state.Credential.OrgId);
        }
    }

    [Fact]
    public void ExpiredV1OAuth_IsReportedAsExpired()
    {
        var (store, auth, _) = Create();
        using (store)
        {
            store.WriteAuthJson("""{"opencode":{"type":"oauth","access":"v1-access","expires":1000}}""");

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.Expired, state.Status);
        }
    }

    [Fact]
    public void V2OAuth_PrefersNewestActiveRowAndIgnoresOtherIntegrations()
    {
        var (store, auth, db) = Create();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("old", "opencode",
                """{"type":"oauth","access":"old-token","expires":1893456000000,"metadata":{"orgID":"old-org"}}""",
                Now.AddMinutes(-10).ToUnixTimeMilliseconds());
            db.InsertCredential("new", "opencode",
                """{"type":"oauth","access":"new-token","expires":1893456000000,"metadata":{"orgID":"new-org"}}""",
                Now.AddMinutes(-1).ToUnixTimeMilliseconds());
            db.InsertCredential("other", "some-other-provider",
                """{"type":"oauth","access":"wrong-token","metadata":{"orgID":"wrong-org"}}""",
                Now.ToUnixTimeMilliseconds());
            db.InsertCredential("inactive", "opencode",
                """{"type":"oauth","access":"inactive-token","metadata":{"orgID":"inactive-org"}}""",
                Now.AddMinutes(-0).ToUnixTimeMilliseconds(), active: 0);

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.Usable, state.Status);
            Assert.Equal("new-token", state.Credential!.Token);
            Assert.Equal("new-org", state.Credential.OrgId);
        }
    }

    [Fact]
    public void V2ApiKey_IsSupported()
    {
        var (store, auth, db) = Create();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("key", "opencode",
                """{"type":"key","key":"console-key","metadata":{"orgID":"key-org","server":"https://opencode.ai/console"}}""",
                Now.ToUnixTimeMilliseconds());

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.Usable, state.Status);
            Assert.Equal(OpenCodeCredentialKind.ApiKey, state.Credential!.Kind);
            Assert.Equal("console-key", state.Credential.Token);
        }
    }

    [Fact]
    public void ExpiredV2OAuth_IsReportedInsteadOfFallingBackToAnotherWorkspace()
    {
        var (store, auth, db) = Create();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("older-valid", "opencode",
                """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
                Now.AddMinutes(-10).ToUnixTimeMilliseconds());
            db.InsertCredential("newest-expired", "opencode",
                """{"type":"oauth","access":"expired-token","expires":1000,"metadata":{"orgID":"newest-org"}}""",
                Now.ToUnixTimeMilliseconds());

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.Expired, state.Status);
            Assert.Equal("expired-token", state.Credential!.Token);
            Assert.Equal("newest-org", state.Credential.OrgId);
        }
    }

    [Fact]
    public void NewestInvalidV2Row_IsReportedInsteadOfBorrowingAnOlderWorkspace()
    {
        var (store, auth, db) = Create();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("older-valid", "opencode",
                """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
                Now.AddMinutes(-10).ToUnixTimeMilliseconds());
            db.InsertCredential("newest-invalid", "opencode",
                """{"type":"oauth","metadata":{"orgID":"newest-org"}}""",
                Now.ToUnixTimeMilliseconds());

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.Invalid, state.Status);
            Assert.Null(state.Credential);
        }
    }

    [Fact]
    public void NewestEmptyV2Row_IsReportedInsteadOfBorrowingAnOlderWorkspace()
    {
        var (store, auth, db) = Create();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("older-valid", "opencode",
                """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
                Now.AddMinutes(-10).ToUnixTimeMilliseconds());
            db.InsertCredential("newest-empty", "opencode", "", Now.ToUnixTimeMilliseconds());

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.Invalid, state.Status);
            Assert.Null(state.Credential);
        }
    }

    [Fact]
    public void NewestUntrustedV2Row_IsReportedInsteadOfBorrowingAnOlderWorkspace()
    {
        var (store, auth, db) = Create();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("older-valid", "opencode",
                """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
                Now.AddMinutes(-10).ToUnixTimeMilliseconds());
            db.InsertCredential("newest-untrusted", "opencode",
                """{"type":"oauth","access":"evil-token","expires":1893456000000,"metadata":{"server":"https://evil.example","orgID":"newest-org"}}""",
                Now.ToUnixTimeMilliseconds());

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.UntrustedOrigin, state.Status);
        }
    }

    [Fact]
    public void NewestUnusableAcrossDatabases_WinsBeforeUsabilityValidation()
    {
        using var store = new OpenCodeTestStore();
        var older = store.Database("opencode.db");
        var newer = store.Database("opencode-next.db");
        older.CreateCredentialSchema();
        newer.CreateCredentialSchema();
        older.InsertCredential("older-valid", "opencode",
            """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
            Now.AddMinutes(-10).ToUnixTimeMilliseconds());
        newer.InsertCredential("newest-expired", "opencode",
            """{"type":"oauth","access":"newest-token","expires":1000,"metadata":{"orgID":"newest-org"}}""",
            Now.ToUnixTimeMilliseconds());
        var auth = AuthFor(store, older, newer);

        var state = auth.LoadConsoleCredential();

        Assert.Equal(OpenCodeCredentialStatus.Expired, state.Status);
        Assert.Equal("newest-token", state.Credential!.Token);
    }

    [Fact]
    public void NewestInvalidAcrossDatabases_DoesNotBorrowOlderValidRow()
    {
        using var store = new OpenCodeTestStore();
        var older = store.Database("opencode.db");
        var newer = store.Database("opencode-next.db");
        older.CreateCredentialSchema();
        newer.CreateCredentialSchema();
        older.InsertCredential("older-valid", "opencode",
            """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
            Now.AddMinutes(-10).ToUnixTimeMilliseconds());
        newer.InsertCredential("newest-invalid", "opencode",
            """{"type":"oauth","metadata":{"orgID":"newest-org"}}""",
            Now.ToUnixTimeMilliseconds());
        var auth = AuthFor(store, older, newer);

        var state = auth.LoadConsoleCredential();

        Assert.Equal(OpenCodeCredentialStatus.Invalid, state.Status);
        Assert.Null(state.Credential);
    }

    [Fact]
    public void NewestUntrustedAcrossDatabases_DoesNotBorrowOlderValidRow()
    {
        using var store = new OpenCodeTestStore();
        var older = store.Database("opencode.db");
        var newer = store.Database("opencode-next.db");
        older.CreateCredentialSchema();
        newer.CreateCredentialSchema();
        older.InsertCredential("older-valid", "opencode",
            """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
            Now.AddMinutes(-10).ToUnixTimeMilliseconds());
        newer.InsertCredential("newest-untrusted", "opencode",
            """{"type":"oauth","access":"newest-token","expires":1893456000000,"metadata":{"server":"https://evil.example","orgID":"newest-org"}}""",
            Now.ToUnixTimeMilliseconds());
        var auth = AuthFor(store, older, newer);

        var state = auth.LoadConsoleCredential();

        Assert.Equal(OpenCodeCredentialStatus.UntrustedOrigin, state.Status);
        Assert.Null(state.Credential);
    }

    [Fact]
    public void V2Expiry_UsesTheInjectedClock()
    {
        using var store = new OpenCodeTestStore();
        var db = store.Database();
        db.CreateCredentialSchema();
        var expiry = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var value = "{\"type\":\"oauth\",\"access\":\"future-for-2010\",\"expires\":"
            + expiry.ToUnixTimeMilliseconds()
            + ",\"metadata\":{\"orgID\":\"org\"}}";
        db.InsertCredential("cred", "opencode", value, Now.ToUnixTimeMilliseconds());
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", store.Root);
        var auth = new OpenCodeAuthStore(
            environment: environment,
            homeDirectory: () => store.Root,
            databasePaths: () => new List<string> { db.Path },
            now: () => new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var state = auth.LoadConsoleCredential();

        Assert.Equal(OpenCodeCredentialStatus.Usable, state.Status);
    }

    [Fact]
    public void UnusableV2Credential_DoesNotFallBackToUsableV1()
    {
        var (store, auth, db) = Create();
        using (store)
        {
            store.WriteAuthJson("""
                {"opencode":{"type":"oauth","access":"v1-access","expires":1893456000000,"metadata":{"orgID":"v1-org"}}}
                """);
            db.CreateCredentialSchema();
            db.InsertCredential("expired", "opencode",
                """{"type":"oauth","access":"v2-expired","expires":1000,"metadata":{"orgID":"v2-org"}}""",
                Now.ToUnixTimeMilliseconds());

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.Expired, state.Status);
            Assert.Equal("v2-expired", state.Credential!.Token);
        }
    }

    [Fact]
    public void UnreadableV2Store_DoesNotFallBackToV1()
    {
        using var store = new OpenCodeTestStore();
        store.WriteAuthJson("""
            {"opencode":{"type":"oauth","access":"v1-access","expires":1893456000000,"metadata":{"orgID":"v1-org"}}}
            """);
        var auth = new OpenCodeAuthStore(
            environment: new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", store.Root),
            homeDirectory: () => store.Root,
            sqlite: new ThrowingSqlite(),
            databasePaths: () => new List<string> { "broken.db" },
            now: () => Now);

        var state = auth.LoadConsoleCredential();

        Assert.Equal(OpenCodeCredentialStatus.Unreadable, state.Status);
        Assert.Null(state.Credential);
    }

    [Fact]
    public void UntrustedMetadataOrigin_IsRejected()
    {
        var (store, auth, db) = Create();
        using (store)
        {
            db.CreateCredentialSchema();
            db.InsertCredential("bad", "opencode",
                """{"type":"oauth","access":"token","metadata":{"server":"https://evil.example","orgID":"org"}}""",
                Now.ToUnixTimeMilliseconds());

            var state = auth.LoadConsoleCredential();

            Assert.Equal(OpenCodeCredentialStatus.UntrustedOrigin, state.Status);
        }
    }

    [Fact]
    public void InvalidAuthJson_IsUnreadableButDoesNotThrowFromConsoleLoader()
    {
        var (store, auth, _) = Create();
        using (store)
        {
            store.WriteAuthJson("{not json");

            Assert.Throws<OpenCodeUsageError>(() => auth.GoApiKey());
            Assert.Equal(OpenCodeCredentialStatus.Unreadable, auth.LoadConsoleCredential().Status);
        }
    }

    [Fact]
    public void MissingEverything_IsNone()
    {
        var (store, auth, _) = Create();
        using (store)
        {
            Assert.Null(auth.GoApiKey());
            Assert.Equal(OpenCodeCredentialStatus.None, auth.LoadConsoleCredential().Status);
            Assert.False(auth.HasAnyLocalCredential());
        }
    }

    [Fact]
    public void CredentialToString_DoesNotExposeSecret()
    {
        var credential = new OpenCodeCredential(
            OpenCodeCredentialKind.OAuth, "super-secret-token", "org", null, OpenCodeConsoleOrigin.Canonical);

        Assert.DoesNotContain("super-secret-token", credential.ToString());
    }

    [Fact]
    public void ReadableOldDatabasePlusUnreadableSecondDatabase_ReturnsUnreadableInsteadOfBorrowingOlderWorkspaceOrV1()
    {
        using var store = new OpenCodeTestStore();
        store.WriteAuthJson("""
            {"opencode":{"type":"oauth","access":"v1-access","expires":1893456000000,"metadata":{"orgID":"v1-org"}}}
            """);
        var older = store.Database("opencode.db");
        older.CreateCredentialSchema();
        older.InsertCredential("older-valid", "opencode",
            """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
            Now.AddMinutes(-10).ToUnixTimeMilliseconds());

        var sqlite = new SelectiveFailingSqlite("opencode-next.db");
        var auth = new OpenCodeAuthStore(
            environment: new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", store.Root),
            homeDirectory: () => store.Root,
            sqlite: sqlite,
            databasePaths: () => new List<string> { older.Path, Path.Combine(store.Root, "opencode-next.db") },
            now: () => Now);

        var state = auth.LoadConsoleCredential();

        Assert.Equal(OpenCodeCredentialStatus.Unreadable, state.Status);
        Assert.Null(state.Credential);
    }

    [Fact]
    public void ReadableDatabasePlusMalformedCredentialWrapper_ReturnsInvalidInsteadOfBorrowingOlderWorkspace()
    {
        using var store = new OpenCodeTestStore();
        var older = store.Database("opencode.db");
        var newer = store.Database("opencode-next.db");
        older.CreateCredentialSchema();
        newer.CreateCredentialSchema();
        older.InsertCredential("older-valid", "opencode",
            """{"type":"oauth","access":"older-token","expires":1893456000000,"metadata":{"orgID":"older-org"}}""",
            Now.AddMinutes(-10).ToUnixTimeMilliseconds());
        // Insert a row whose time_updated is NULL so its wrapper lacks a usable timestamp ordering.
        newer.Execute(
            "INSERT INTO credential (id, integration_id, label, value, method_id, active, time_created, time_updated) VALUES ('corrupt-ts', 'opencode', 'test', @val, 'device', 1, NULL, NULL);",
            ("@val", """{"type":"oauth","access":"newer-token","expires":1893456000000,"metadata":{"orgID":"newer-org"}}"""));

        var auth = AuthFor(store, older, newer);

        var state = auth.LoadConsoleCredential();

        Assert.Equal(OpenCodeCredentialStatus.Invalid, state.Status);
        Assert.Null(state.Credential);
    }

    private sealed class SelectiveFailingSqlite : ISqliteAccessing
    {
        private readonly string _failingFileName;
        private readonly SqliteDataAccessor _inner = new();

        public SelectiveFailingSqlite(string failingFileName) => _failingFileName = failingFileName;

        public string? QueryValue(string path, string sql) =>
            path.EndsWith(_failingFileName, StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("database is locked")
                : _inner.QueryValue(path, sql);

        public void Execute(string path, string sql) => _inner.Execute(path, sql);
    }

    private sealed class ThrowingSqlite : ISqliteAccessing
    {
        public string? QueryValue(string path, string sql) => throw new InvalidOperationException("unreadable");
        public void Execute(string path, string sql) => throw new NotSupportedException();
    }
}
