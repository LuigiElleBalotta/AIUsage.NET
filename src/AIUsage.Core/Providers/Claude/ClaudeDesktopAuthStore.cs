using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Claude;

/// <summary>
/// Reads Claude Desktop's Electron OAuth cache as an externally owned, read-only fallback credential
/// source, used when Claude Code's own CLI credentials (file/Credential Manager) aren't found. Direct
/// port of the Swift <c>ClaudeDesktopAuthStore</c>, adapted to Electron's Windows key-storage scheme:
/// macOS Keychain → Windows DPAPI (via the Chromium/Electron "os_crypt" convention every Electron app
/// shares — the same scheme Chrome/Edge use for cookies), keyed off Claude Desktop's own
/// <c>Local State</c>/<c>config.json</c>/cookie-database files instead of the macOS bundle layout.
///
/// The refresh token is deliberately never read: Anthropic rotates refresh tokens, so borrowing one
/// here would invalidate Desktop's own copy. Only a currently-valid access token is borrowed, and
/// Desktop is left to renew it on its own schedule.
///
/// <b>Unverified against a real Windows Claude Desktop install</b> — ported from documented
/// Electron/Chromium os_crypt behavior (DPAPI-wrapped AES-256-GCM key in <c>Local State</c>,
/// <c>v10</c>/<c>v11</c>-prefixed ciphertext) since Claude Desktop for Windows was not available to
/// test against during this port. Fails closed (returns no credential, never throws past this store)
/// if any expected file/shape is missing, so a wrong guess about the exact install layout degrades to
/// "Desktop fallback unavailable" rather than crashing the provider.
/// </summary>
public sealed class ClaudeDesktopAuthStore
{
    // Electron's default userData directory is %APPDATA%\<productName>. Claude Desktop has shipped
    // under a plain "Claude" folder in most reported installs; a packaged (MSIX/Store) install can
    // instead virtualize AppData under Packages\<PackageFamilyName>\LocalCache\Roaming\Claude — both
    // are tried, first match wins.
    private static readonly string[] CandidateDirectoryNames = { "Claude" };
    private const string PackagedDirectorySearchPattern = "Claude_*";

    private const string ConfigFileName = "config.json";
    private const string LocalStateFileName = "Local State";
    private static readonly string[] CookieRelativePaths = { "Cookies", @"Network\Cookies" };
    private const string CacheV1Key = "oauth:tokenCache";
    private const string CacheV2Key = "oauth:tokenCacheV2";
    private const string ApiHost = "api.anthropic.com";
    private const string UsageScope = "user:profile";
    private const string InferenceScope = "user:inference";
    private const double ExpirySafetyMarginMs = 2 * 60 * 1000.0;
    private static readonly string[] CookieHosts = { ".claude.ai", "claude.ai" };

    /// The OAuth client ID Claude's production login mints full-scope tokens under (same value as
    /// ClaudeAuthStore.ProdClientId) — Desktop's cache can hold several entries per org (partial-scope
    /// leftovers from older logins included), and this is how Desktop itself resolves the active one.
    private const string ProductionClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private readonly ITextFileAccessing _files;
    private readonly ISqliteAccessing _sqlite;
    private readonly Func<string> _appDataDirectory;
    private readonly Func<string> _localAppDataDirectory;
    private readonly Func<DateTimeOffset> _now;

    public ClaudeDesktopAuthStore(
        ITextFileAccessing? files = null,
        ISqliteAccessing? sqlite = null,
        Func<string>? appDataDirectory = null,
        Func<string>? localAppDataDirectory = null,
        Func<DateTimeOffset>? now = null)
    {
        _files = files ?? new LocalTextFileAccessor();
        _sqlite = sqlite ?? new SqliteDataAccessor();
        _appDataDirectory = appDataDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        _localAppDataDirectory = localAppDataDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Cheap, decrypt-free evidence for first-run detection: does a Claude Desktop install
    /// with an OAuth cache and a cookie database actually exist on this machine.</summary>
    public bool HasCredentialMaterial()
    {
        var directory = FindInstallDirectory();
        if (directory is null) return false;
        var configText = _files.ReadTextIfPresent(Path.Combine(directory, ConfigFileName));
        if (configText is null) return false;
        if (ParseJsonObject(configText) is not { } root) return false;
        var hasCache = root.TryGetProperty(CacheV2Key, out var v2) && v2.ValueKind == JsonValueKind.String
                       || root.TryGetProperty(CacheV1Key, out var v1) && v1.ValueKind == JsonValueKind.String;
        if (!hasCache) return false;
        return CookieRelativePaths.Any(p => _files.Exists(Path.Combine(directory, p)));
    }

    /// <summary>Attempts to read a currently-valid Claude Desktop access token. Returns null on any
    /// failure (no install, no key, decrypt failure, no valid token) — this store never throws, since
    /// it is purely an optional fallback.</summary>
    public ClaudeCredentialState? Load()
    {
        try
        {
            var directory = FindInstallDirectory();
            if (directory is null) return null;

            var key = ReadOsCryptKey(directory);
            if (key is null) return null;

            var activeOrg = LoadActiveOrganization(directory, key);
            if (activeOrg is null) return null;

            var configText = _files.ReadTextIfPresent(Path.Combine(directory, ConfigFileName));
            if (configText is null || ParseJsonObject(configText) is not { } root) return null;

            var v2 = DecodeCache(root, CacheV2Key, key);
            var v1 = DecodeCache(root, CacheV1Key, key);

            var best = SelectCredential(activeOrg, v2, v1, _now());
            if (best is null) return null;

            return new ClaudeCredentialState
            {
                OAuth = best,
                Source = new ClaudeCredentialSource(ClaudeCredentialSourceKind.Environment) // no dedicated kind exists yet; treated as read-only/non-persistable, same as the env-token path
            };
        }
        catch (Exception ex)
        {
            AppLog.Debug(LogTag.AuthFor("claude"), $"Claude Desktop credential read failed: {ex.Message}");
            return null;
        }
    }

    // MARK: - Install discovery

    private string? FindInstallDirectory()
    {
        var appData = _appDataDirectory();
        foreach (var name in CandidateDirectoryNames)
        {
            var direct = Path.Combine(appData, name);
            if (_files.Exists(Path.Combine(direct, ConfigFileName))) return direct;
        }

        // Packaged/virtualized AppData: %LOCALAPPDATA%\Packages\Claude_<hash>\LocalCache\Roaming\Claude
        var localAppData = _localAppDataDirectory();
        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (!Directory.Exists(packagesRoot)) return null;
        try
        {
            foreach (var package in Directory.EnumerateDirectories(packagesRoot, PackagedDirectorySearchPattern))
            {
                var candidate = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                if (_files.Exists(Path.Combine(candidate, ConfigFileName))) return candidate;
            }
        }
        catch
        {
            // best-effort discovery only
        }
        return null;
    }

    // MARK: - os_crypt key (DPAPI-wrapped AES-256 key stored in "Local State")

    private byte[]? ReadOsCryptKey(string directory)
    {
        var text = _files.ReadTextIfPresent(Path.Combine(directory, LocalStateFileName));
        if (text is null || ParseJsonObject(text) is not { } root) return null;
        if (!root.TryGetProperty("os_crypt", out var osCrypt) || osCrypt.ValueKind != JsonValueKind.Object) return null;
        if (!osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyEl) || encryptedKeyEl.ValueKind != JsonValueKind.String) return null;

        byte[] wrapped;
        try
        {
            wrapped = Convert.FromBase64String(encryptedKeyEl.GetString() ?? "");
        }
        catch
        {
            return null;
        }
        // The base64-decoded blob is ASCII "DPAPI" (5 bytes) followed by the actual DPAPI-protected key.
        const string prefix = "DPAPI";
        if (wrapped.Length <= prefix.Length) return null;
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        if (!wrapped.AsSpan(0, prefixBytes.Length).SequenceEqual(prefixBytes)) return null;

        var dpapiBlob = wrapped[prefixBytes.Length..];
        try
        {
            return ProtectedData.Unprotect(dpapiBlob, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // MARK: - Active organization (from the "lastActiveOrg" cookie)

    private string? LoadActiveOrganization(string directory, byte[] key)
    {
        foreach (var relativePath in CookieRelativePaths)
        {
            var dbPath = Path.Combine(directory, relativePath);
            if (!_files.Exists(dbPath)) continue;

            foreach (var host in CookieHosts)
            {
                var hostSql = host.Replace("'", "''");
                var sql = $"""
                    SELECT CASE
                        WHEN length(value) > 0 THEN 'plain:' || hex(CAST(value AS BLOB))
                        ELSE 'encrypted:' || hex(encrypted_value)
                    END
                    FROM cookies
                    WHERE name = 'lastActiveOrg' AND host_key = '{hostSql}'
                    ORDER BY last_update_utc DESC
                    LIMIT 1;
                    """;
                string? encoded;
                try { encoded = _sqlite.QueryValue(dbPath, sql); } catch { encoded = null; }
                if (encoded is null) continue;

                var separator = encoded.IndexOf(':');
                if (separator < 0) continue;
                var mode = encoded[..separator];
                var hexValue = encoded[(separator + 1)..];
                var stored = FromHex(hexValue);
                if (stored is null) continue;

                byte[] value;
                if (mode == "plain")
                {
                    value = stored;
                }
                else if (mode == "encrypted")
                {
                    if (DecryptOsCryptValue(stored, key) is not { } decrypted) continue;
                    var hostHash = SHA256.HashData(Encoding.UTF8.GetBytes(host));
                    if (decrypted.Length < hostHash.Length || !decrypted.AsSpan(0, hostHash.Length).SequenceEqual(hostHash)) continue;
                    value = decrypted[hostHash.Length..];
                }
                else
                {
                    continue;
                }

                var organization = Encoding.UTF8.GetString(value);
                if (Guid.TryParse(organization, out var parsed)) return parsed.ToString().ToLowerInvariant();
            }
        }
        return null;
    }

    // MARK: - OAuth cache decode

    private Dictionary<string, JsonElement>? DecodeCache(JsonElement root, string key, byte[] cryptKey)
    {
        if (!root.TryGetProperty(key, out var stored) || stored.ValueKind != JsonValueKind.String) return null;
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(stored.GetString() ?? ""); } catch { return null; }
        if (DecryptOsCryptValue(encrypted, cryptKey) is not { } plaintext) return null;
        try
        {
            using var doc = JsonDocument.Parse(plaintext);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch
        {
            return null;
        }
    }

    private sealed record Candidate(ClaudeOAuth OAuth, string ClientId, List<string> Scopes, double ExpiresAt)
    {
        /// Selection order mirrors Desktop's own resolution instead of raw expiry: production client
        /// with full scopes first, then any full-scope entry over bare user:profile leftovers, then
        /// scope richness, with expiry only as the final tiebreak.
        public (int, int, int, double) Rank
        {
            get
            {
                var hasFullScope = Scopes.Contains(UsageScope) && Scopes.Contains(InferenceScope);
                var isProductionClient = ClientId == ProductionClientId;
                return (isProductionClient && hasFullScope ? 1 : 0, hasFullScope ? 1 : 0, Scopes.Count, ExpiresAt);
            }
        }
    }

    private static ClaudeOAuth? SelectCredential(string activeOrganization, Dictionary<string, JsonElement>? v2, Dictionary<string, JsonElement>? v1, DateTimeOffset now)
    {
        var normalizedOrg = activeOrganization.ToLowerInvariant();
        var v2Candidates = Candidates(v2, normalizedOrg, now);
        var best = v2Candidates.OrderByDescending(c => c.Rank).FirstOrDefault();
        if (best is not null) return best.OAuth;

        var v2Keys = new HashSet<string>(v2?.Keys ?? Enumerable.Empty<string>());
        var v1Filtered = v1?.Where(kv => !v2Keys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var v1Candidates = Candidates(v1Filtered, normalizedOrg, now);
        best = v1Candidates.OrderByDescending(c => c.Rank).FirstOrDefault();
        return best?.OAuth;
    }

    private static List<Candidate> Candidates(Dictionary<string, JsonElement>? cache, string organization, DateTimeOffset now)
    {
        var result = new List<Candidate>();
        if (cache is null) return result;

        foreach (var (cacheKey, entry) in cache)
        {
            var parsedKey = ParseCacheKey(cacheKey);
            if (parsedKey is null || parsedKey.Organization != organization || parsedKey.ApiHost != ApiHost || !parsedKey.Scopes.Contains(UsageScope)) continue;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("token", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String) continue;
            var token = tokenEl.GetString();
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (!entry.TryGetProperty("expiresAt", out var expiresAtEl) || ProviderParse.Number(expiresAtEl) is not { } expiresAt || !double.IsFinite(expiresAt)) continue;
            if (!(expiresAt > now.ToUnixTimeMilliseconds() + ExpirySafetyMarginMs)) continue; // stale, skip

            var subscriptionType = entry.TryGetProperty("subscriptionType", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            var rateLimitTier = entry.TryGetProperty("rateLimitTier", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;

            var oauth = new ClaudeOAuth
            {
                AccessToken = token,
                RefreshToken = null,
                ExpiresAt = expiresAt,
                SubscriptionType = subscriptionType,
                RateLimitTier = rateLimitTier,
                Scopes = parsedKey.Scopes
            };
            result.Add(new Candidate(oauth, parsedKey.ClientId, parsedKey.Scopes, expiresAt));
        }
        return result;
    }

    private sealed record CacheKey(string ClientId, string Organization, string ApiHost, List<string> Scopes);

    /// Cache keys look like "<clientId>:<organization>:<apiHost>:<space-separated scopes>".
    private static CacheKey? ParseCacheKey(string value)
    {
        var marker = $":{ApiHost}:";
        var markerIndex = value.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return null;
        var prefix = value[..markerIndex];
        var firstColon = prefix.IndexOf(':');
        if (firstColon < 0) return null;
        var clientId = prefix[..firstColon];
        var organization = prefix[(firstColon + 1)..].ToLowerInvariant();
        if (!Guid.TryParse(clientId, out _) || !Guid.TryParse(organization, out _)) return null;
        var scopes = value[(markerIndex + marker.Length)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        return new CacheKey(clientId, organization, ApiHost, scopes);
    }

    // MARK: - AES-256-GCM decrypt (Chromium/Electron os_crypt "v10"/"v11" convention)

    private static byte[]? DecryptOsCryptValue(byte[] encrypted, byte[] key)
    {
        const int versionPrefixLength = 3; // "v10" or "v11"
        const int nonceLength = 12;
        const int tagLength = 16;
        if (encrypted.Length <= versionPrefixLength + nonceLength + tagLength) return null;

        var version = Encoding.ASCII.GetString(encrypted, 0, versionPrefixLength);
        if (version != "v10" && version != "v11") return null;

        var nonce = encrypted.AsSpan(versionPrefixLength, nonceLength);
        var cipherAndTag = encrypted.AsSpan(versionPrefixLength + nonceLength);
        var ciphertext = cipherAndTag[..^tagLength];
        var tag = cipherAndTag[^tagLength..];

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aesGcm = new AesGcm(key, tagLength);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[]? FromHex(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        try
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? ParseJsonObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch
        {
            return null;
        }
    }
}
