using System.Security.Cryptography;
using System.Text;
using AIUsage.Core.Providers.Claude;
using AIUsage.Core.Services;
using Microsoft.Data.Sqlite;

namespace AIUsage.Core.Tests.Providers;

/// <summary>End-to-end tests against a synthetic Claude Desktop install: real DPAPI (runs as the
/// current user, same as production), real AES-256-GCM encryption matching Chromium/Electron's
/// os_crypt "v10" convention, and a real temporary SQLite cookie database — everything
/// ClaudeDesktopAuthStore actually reads, built by hand instead of mocked, so the test exercises the
/// real crypto/parsing path end to end.</summary>
public class ClaudeDesktopAuthStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly byte[] _cryptKey;
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);
    private const string OrgId = "11111111-2222-3333-4444-555555555555";
    private const string ProductionClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    public ClaudeDesktopAuthStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AIUsageClaudeDesktopTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _cryptKey = RandomNumberGenerator.GetBytes(32);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private ClaudeDesktopAuthStore MakeStore() => new(
        files: new LocalTextFileAccessor(),
        sqlite: new SqliteDataAccessor(),
        appDataDirectory: () => _tempDir,
        // Isolate the "packaged install" fallback search from this machine's real
        // %LOCALAPPDATA%\Packages (which may contain a genuine Claude Desktop install).
        localAppDataDirectory: () => Path.Combine(_tempDir, "LocalAppDataIsolated"),
        now: () => Now);

    private void WriteLocalState()
    {
        var dpapiProtected = ProtectedData.Protect(_cryptKey, null, DataProtectionScope.CurrentUser);
        var wrapped = Encoding.ASCII.GetBytes("DPAPI").Concat(dpapiProtected).ToArray();
        var encodedKey = Convert.ToBase64String(wrapped);
        File.WriteAllText(Path.Combine(_tempDir, "Claude", "Local State"),
            "{\"os_crypt\":{\"encrypted_key\":\"" + encodedKey + "\"}}");
    }

    private string EncryptOsCryptValue(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aesGcm = new AesGcm(_cryptKey, 16);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        var combined = Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(ciphertext).Concat(tag).ToArray();
        return Convert.ToBase64String(combined);
    }

    private void WriteConfig(string cacheKey, string tokenJson, string cacheProperty = "oauth:tokenCacheV2")
    {
        var expiresAt = Now.ToUnixTimeMilliseconds() + 3_600_000;
        var cachePayload = "{\"" + cacheKey + "\":{\"token\":\"" + tokenJson + "\",\"expiresAt\":" + expiresAt + "}}";
        var encryptedCache = EncryptOsCryptValue(Encoding.UTF8.GetBytes(cachePayload));
        File.WriteAllText(Path.Combine(_tempDir, "Claude", "config.json"),
            "{\"" + cacheProperty + "\":\"" + encryptedCache + "\"}");
    }

    private void WriteCookieDb(string organizationId, bool encrypted)
    {
        var dbPath = Path.Combine(_tempDir, "Claude", "Cookies");
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE cookies (name TEXT, host_key TEXT, value TEXT, encrypted_value BLOB, last_update_utc INTEGER)";
            create.ExecuteNonQuery();
        }

        if (encrypted)
        {
            var hostHash = SHA256.HashData(Encoding.UTF8.GetBytes(".claude.ai"));
            var payload = hostHash.Concat(Encoding.UTF8.GetBytes(organizationId)).ToArray();
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[payload.Length];
            var tag = new byte[16];
            using var aesGcm = new AesGcm(_cryptKey, 16);
            aesGcm.Encrypt(nonce, payload, ciphertext, tag);
            var combined = Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(ciphertext).Concat(tag).ToArray();

            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO cookies (name, host_key, value, encrypted_value, last_update_utc) VALUES ('lastActiveOrg', '.claude.ai', '', @blob, 1)";
            insert.Parameters.AddWithValue("@blob", combined);
            insert.ExecuteNonQuery();
        }
        else
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO cookies (name, host_key, value, encrypted_value, last_update_utc) VALUES ('lastActiveOrg', '.claude.ai', @value, x'', 1)";
            insert.Parameters.AddWithValue("@value", organizationId);
            insert.ExecuteNonQuery();
        }
    }

    private static string CacheKey(string clientId, string org, List<string> scopes) =>
        $"{clientId}:{org}:api.anthropic.com:{string.Join(" ", scopes)}";

    [Fact]
    public void HasCredentialMaterial_NoInstall_ReturnsFalse()
    {
        var store = MakeStore();
        Assert.False(store.HasCredentialMaterial());
    }

    [Fact]
    public void HasCredentialMaterial_ConfigWithoutCookies_ReturnsFalse()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Claude"));
        File.WriteAllText(Path.Combine(_tempDir, "Claude", "config.json"), """{"oauth:tokenCacheV2":"abc"}""");
        var store = MakeStore();
        Assert.False(store.HasCredentialMaterial());
    }

    [Fact]
    public void HasCredentialMaterial_ConfigAndCookiesPresent_ReturnsTrue()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Claude"));
        File.WriteAllText(Path.Combine(_tempDir, "Claude", "config.json"), """{"oauth:tokenCacheV2":"abc"}""");
        File.WriteAllText(Path.Combine(_tempDir, "Claude", "Cookies"), "not-a-real-db-but-file-exists-check-only");
        var store = MakeStore();
        Assert.True(store.HasCredentialMaterial());
    }

    [Fact]
    public void Load_FullPipeline_WithPlainOrgCookie_ReturnsAccessToken()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Claude"));
        WriteLocalState();
        var scopes = new List<string> { "user:profile", "user:inference" };
        var cacheKey = CacheKey(ProductionClientId, OrgId, scopes);
        WriteConfig(cacheKey, "sk-ant-real-token-value");
        WriteCookieDb(OrgId, encrypted: false);

        var store = MakeStore();
        var state = store.Load();

        Assert.NotNull(state);
        Assert.Equal("sk-ant-real-token-value", state!.OAuth.AccessToken);
        Assert.Null(state.OAuth.RefreshToken); // deliberately never read
    }

    [Fact]
    public void Load_FullPipeline_WithEncryptedOrgCookie_ReturnsAccessToken()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Claude"));
        WriteLocalState();
        var scopes = new List<string> { "user:profile", "user:inference" };
        var cacheKey = CacheKey(ProductionClientId, OrgId, scopes);
        WriteConfig(cacheKey, "sk-ant-encrypted-cookie-token");
        WriteCookieDb(OrgId, encrypted: true);

        var store = MakeStore();
        var state = store.Load();

        Assert.NotNull(state);
        Assert.Equal("sk-ant-encrypted-cookie-token", state!.OAuth.AccessToken);
    }

    [Fact]
    public void Load_NoMatchingOrganization_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Claude"));
        WriteLocalState();
        var scopes = new List<string> { "user:profile", "user:inference" };
        var differentOrg = "99999999-8888-7777-6666-555555555555";
        var cacheKey = CacheKey(ProductionClientId, differentOrg, scopes); // cache entry for a DIFFERENT org
        WriteConfig(cacheKey, "sk-ant-token");
        WriteCookieDb(OrgId, encrypted: false); // active org cookie names OrgId

        var store = MakeStore();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ExpiredToken_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Claude"));
        WriteLocalState();
        var scopes = new List<string> { "user:profile", "user:inference" };
        var cacheKey = CacheKey(ProductionClientId, OrgId, scopes);
        // Manually write an already-expired entry instead of using WriteConfig's fixed +1h expiry.
        var expiredAt = Now.AddHours(-1).ToUnixTimeMilliseconds();
        var cachePayload = "{\"" + cacheKey + "\":{\"token\":\"sk-ant-expired\",\"expiresAt\":" + expiredAt + "}}";
        var encryptedCache = EncryptOsCryptValue(Encoding.UTF8.GetBytes(cachePayload));
        File.WriteAllText(Path.Combine(_tempDir, "Claude", "config.json"), "{\"oauth:tokenCacheV2\":\"" + encryptedCache + "\"}");
        WriteCookieDb(OrgId, encrypted: false);

        var store = MakeStore();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_MissingProfileScope_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Claude"));
        WriteLocalState();
        // Cache entry lacks "user:profile" - ClaudeDesktopAuthStore requires it (usage endpoint scope).
        var cacheKey = CacheKey(ProductionClientId, OrgId, new List<string> { "user:inference" });
        WriteConfig(cacheKey, "sk-ant-inference-only");
        WriteCookieDb(OrgId, encrypted: false);

        var store = MakeStore();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_NoLocalState_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Claude"));
        // config.json and Cookies exist, but no "Local State" (no os_crypt key) - can't decrypt anything.
        File.WriteAllText(Path.Combine(_tempDir, "Claude", "config.json"), """{"oauth:tokenCacheV2":"irrelevant"}""");
        WriteCookieDb(OrgId, encrypted: false);

        var store = MakeStore();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_V1PreferredOverV2WhenV2Missing()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Claude"));
        WriteLocalState();
        var scopes = new List<string> { "user:profile", "user:inference" };
        var cacheKey = CacheKey(ProductionClientId, OrgId, scopes);
        WriteConfig(cacheKey, "sk-ant-v1-token", cacheProperty: "oauth:tokenCache");
        WriteCookieDb(OrgId, encrypted: false);

        var store = MakeStore();
        var state = store.Load();

        Assert.NotNull(state);
        Assert.Equal("sk-ant-v1-token", state!.OAuth.AccessToken);
    }
}
