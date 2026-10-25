using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

public class OpenCodePathsTests
{
    private const string Home = "C:/Users/tester";
    private const string LegacyLocalAppData = "C:/Users/tester/AppData/Local";

    private static string Canonical(string home = Home) =>
        Path.Combine(home, ".local", "share", "opencode");

    private static string Legacy(string localAppData = LegacyLocalAppData) =>
        Path.Combine(localAppData, "opencode");

    private static string Resolve(
        InMemoryEnvironment environment,
        Func<string, bool> directoryExists,
        string home = Home,
        string localAppData = LegacyLocalAppData,
        Func<string, string>? expandHome = null) =>
        OpenCodePaths.DataDirectory(
            environment,
            home,
            directoryExists: directoryExists,
            localAppDataDirectory: () => localAppData,
            expandHome: expandHome ?? (path => path));

    [Fact]
    public void DataDirectory_UsesCanonicalHomePath_WhenItExists()
    {
        var result = Resolve(new InMemoryEnvironment(), path => path == Canonical());

        Assert.Equal(Canonical(), result);
    }

    [Fact]
    public void DataDirectory_UsesLegacyLocalAppData_WhenOnlyLegacyExists()
    {
        var result = Resolve(new InMemoryEnvironment(), path => path == Legacy());

        Assert.Equal(Legacy(), result);
    }

    [Fact]
    public void DataDirectory_PrefersCanonicalHomePath_WhenBothExist()
    {
        var result = Resolve(new InMemoryEnvironment(), path => path == Canonical() || path == Legacy());

        Assert.Equal(Canonical(), result);
    }

    [Fact]
    public void DataDirectory_ReturnsCanonicalHomePath_WhenNeitherExists()
    {
        var result = Resolve(new InMemoryEnvironment(), _ => false);

        Assert.Equal(Canonical(), result);
    }

    [Fact]
    public void DataDirectory_OpenCodeDataDirOverride_WinsEvenWhenPathDoesNotExist()
    {
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", "C:/custom/opencode");

        var result = Resolve(environment, path => path == Canonical());

        Assert.Equal("C:/custom/opencode", result);
    }

    [Fact]
    public void DataDirectory_WhitespaceOpenCodeDataDir_FallsBackToDefault()
    {
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", "   ");

        var result = Resolve(environment, path => path == Canonical());

        Assert.Equal(Canonical(), result);
    }

    [Fact]
    public void DataDirectory_OpenCodeDataDir_TrimsWhitespaceAndTrailingSlash()
    {
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", "  C:/custom/opencode/  ");

        var result = Resolve(environment, _ => false);

        Assert.Equal("C:/custom/opencode", result);
    }

    [Fact]
    public void DataDirectory_OpenCodeDataDir_ExpandsHome()
    {
        var environment = new InMemoryEnvironment().Set("OPENCODE_DATA_DIR", "~/scratch/opencode");

        var result = Resolve(environment, _ => false, expandHome: TestExpandHome);

        Assert.Equal(Path.Combine(Home, "scratch", "opencode"), result);
    }

    [Fact]
    public void DataDirectory_XdgDataHome_TakesPrecedenceOverCanonical()
    {
        var environment = new InMemoryEnvironment().Set("XDG_DATA_HOME", "C:/xdg");

        var result = Resolve(environment, path => path == Canonical());

        Assert.Equal(Path.Combine("C:/xdg", "opencode"), result);
    }

    [Fact]
    public void DataDirectory_XdgDataHome_ReturnsPathEvenWhenItDoesNotExist()
    {
        var environment = new InMemoryEnvironment().Set("XDG_DATA_HOME", "C:/missing-xdg");

        var result = Resolve(environment, _ => false);

        Assert.Equal(Path.Combine("C:/missing-xdg", "opencode"), result);
    }

    [Fact]
    public void DataDirectory_WhitespaceXdgDataHome_FallsBackToDefault()
    {
        var environment = new InMemoryEnvironment().Set("XDG_DATA_HOME", "  ");

        var result = Resolve(environment, path => path == Canonical());

        Assert.Equal(Canonical(), result);
    }

    [Fact]
    public void DataDirectory_XdgDataHome_TrimsTrailingSlashAndExpandsHome()
    {
        var environment = new InMemoryEnvironment().Set("XDG_DATA_HOME", "~/.xdg/");

        var result = Resolve(environment, _ => false, expandHome: TestExpandHome);

        Assert.Equal(Path.Combine(Home, ".xdg", "opencode"), result);
    }

    private static string TestExpandHome(string path)
    {
        if (path == "~") return Home;
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(Home, path[2..].Replace('/', Path.DirectorySeparatorChar));
        }
        return path;
    }
}

/// <summary>
/// Proves the production default resolution (no env overrides, no injected database list) feeds
/// both the credential loader and the log scanner from the same canonical directory, using only
/// temporary fixtures instead of the current user's real OpenCode data.
/// </summary>
public sealed class OpenCodeDataDirectoryIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _home;
    private readonly string _canonical;

    public OpenCodeDataDirectoryIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AIUsageOpenCodePathsTests_" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(_tempRoot, "home");
        _canonical = Path.Combine(_home, ".local", "share", "opencode");
        Directory.CreateDirectory(_canonical);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void CanonicalDirectory_FeedsAuthStoreAndScanner()
    {
        File.WriteAllText(Path.Combine(_canonical, "auth.json"), """{"opencode-go":{"key":"sk-test"}}""");
        File.WriteAllText(Path.Combine(_canonical, "opencode.db"), "");
        var environment = new InMemoryEnvironment();

        var authStore = new OpenCodeAuthStore(environment: environment, homeDirectory: () => _home);
        var sqlite = new RecordingSqlite { ProbeResult = "1" };
        var scanner = new OpenCodeUsageScanner(sqlite, environment: environment, homeDirectory: () => _home);

        Assert.Equal(_canonical, authStore.DataDirectory);
        Assert.Equal("sk-test", authStore.GoApiKey());
        Assert.True(scanner.HasHostedUsage());
        Assert.Equal(new[] { Path.Combine(_canonical, "opencode.db") }, sqlite.QueriedPaths.Distinct());
    }

    [Fact]
    public async Task CanonicalDatabaseOnly_FeedsProviderLocalDetection()
    {
        File.WriteAllText(Path.Combine(_canonical, "opencode.db"), "");
        var environment = new InMemoryEnvironment();

        var authStore = new OpenCodeAuthStore(environment: environment, homeDirectory: () => _home);
        var sqlite = new RecordingSqlite { ProbeResult = "1" };
        var scanner = new OpenCodeUsageScanner(sqlite, environment: environment, homeDirectory: () => _home);
        var provider = new OpenCodeProvider(authStore, scanner);

        Assert.True(await provider.HasLocalCredentialsAsync());
        Assert.Equal(new[] { Path.Combine(_canonical, "opencode.db") }, sqlite.QueriedPaths.Distinct());
    }

    private sealed class RecordingSqlite : ISqliteAccessing
    {
        public List<string> QueriedPaths { get; } = new();
        public string? ProbeResult { get; init; }

        public string? QueryValue(string path, string sql)
        {
            QueriedPaths.Add(path);
            if (sql.Contains("sqlite_master", StringComparison.Ordinal)) return "message";
            return sql.Contains("SELECT 1", StringComparison.Ordinal) ? ProbeResult : null;
        }

        public void Execute(string path, string sql) => throw new NotSupportedException();
    }
}
