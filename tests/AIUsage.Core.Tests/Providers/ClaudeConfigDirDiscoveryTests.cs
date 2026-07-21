using AIUsage.Core.Providers.Claude;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

public class ClaudeConfigDirDiscoveryTests
{
    private const string Home = "C:/Users/tester";

    private static ClaudeConfigDirDiscovery MakeDiscovery(
        InMemoryEnvironment env,
        InMemoryFileSystem files,
        InMemoryKeychain keychain,
        Func<string, List<string>>? listSubdirectories = null) =>
        new(env, files, keychain, homeDirectory: () => Home,
            listSubdirectories: listSubdirectories ?? (_ => new List<string>()));

    [Fact]
    public void Run_SkipsDefaultClaudeHome()
    {
        var files = new InMemoryFileSystem().Write($"{Home}/.claude/.claude.json",
            """{"oauthAccount":{"accountUuid":"UUID-1"}}""");
        var discovery = MakeDiscovery(
            new InMemoryEnvironment(), files, new InMemoryKeychain(),
            home => home == Home ? new List<string> { $"{Home}/.claude" } : new List<string>());

        var result = discovery.Run();

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Run_AcceptsCandidate_WithFileBackedCredentials()
    {
        var files = new InMemoryFileSystem()
            .Write($"{Home}/.claude-work/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-2","emailAddress":"b@c.com"}}""")
            .Write($"{Home}/.claude-work/.credentials.json", """{"claudeAiOauth":{"accessToken":"tok"}}""");
        var discovery = MakeDiscovery(
            new InMemoryEnvironment(), files, new InMemoryKeychain(),
            home => home == Home ? new List<string> { $"{Home}/.claude-work" } : new List<string>());

        var result = discovery.Run();

        var finding = Assert.Single(result.Findings);
        Assert.Equal("uuid-2", finding.IdentityKey);
        Assert.Equal("b@c.com", finding.Label);
        Assert.Equal($"{Home}/.claude-work", finding.AnchorPath);
    }

    [Fact]
    public void Run_AcceptsCandidate_WithKeychainBackedCredentials()
    {
        var env = new InMemoryEnvironment();
        var configDirPath = $"{Home}/.claude-work";
        var files = new InMemoryFileSystem().Write($"{configDirPath}/.claude.json",
            """{"oauthAccount":{"accountUuid":"UUID-3"}}""");
        var service = ClaudeAuthStore.ScopedKeychainServiceName(configDirPath, env);
        var keychain = new InMemoryKeychain().Set(service, "present");
        var discovery = MakeDiscovery(env, files, keychain,
            home => home == Home ? new List<string> { configDirPath } : new List<string>());

        var result = discovery.Run();

        var finding = Assert.Single(result.Findings);
        Assert.Equal("uuid-3", finding.IdentityKey);
        Assert.Equal(configDirPath, finding.KeychainLiteral);
    }

    [Fact]
    public void Run_RejectsCandidate_WithIdentityButNoCredentials()
    {
        var files = new InMemoryFileSystem().Write($"{Home}/.claude-orphan/.claude.json",
            """{"oauthAccount":{"accountUuid":"UUID-4"}}""");
        var discovery = MakeDiscovery(
            new InMemoryEnvironment(), files, new InMemoryKeychain(),
            home => home == Home ? new List<string> { $"{Home}/.claude-orphan" } : new List<string>());

        var result = discovery.Run();

        Assert.Empty(result.Findings);
        Assert.Contains(result.Notes, n => n.Contains("no credential"));
    }

    [Fact]
    public void Run_RejectsCandidate_WithoutIdentityFile()
    {
        var files = new InMemoryFileSystem().Write($"{Home}/.random/.credentials.json", "{}");
        var discovery = MakeDiscovery(
            new InMemoryEnvironment(), files, new InMemoryKeychain(),
            home => home == Home ? new List<string> { $"{Home}/.random" } : new List<string>());

        var result = discovery.Run();

        Assert.Empty(result.Findings);
        // Random dot-dirs without an identity file don't even generate a note.
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Run_RejectsCandidate_WhenIdentityFileNamesNoAccount()
    {
        var files = new InMemoryFileSystem().Write($"{Home}/.claude-broken/.claude.json", "{}");
        var discovery = MakeDiscovery(
            new InMemoryEnvironment(), files, new InMemoryKeychain(),
            home => home == Home ? new List<string> { $"{Home}/.claude-broken" } : new List<string>());

        var result = discovery.Run();

        Assert.Empty(result.Findings);
        Assert.Contains(result.Notes, n => n.Contains("names no account"));
    }

    [Fact]
    public void Run_HonorsTimeBudget_ReturningPartialResultsWithNote()
    {
        var files = new InMemoryFileSystem()
            .Write($"{Home}/.claude-a/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-A"}}""")
            .Write($"{Home}/.claude-a/.credentials.json", """{"claudeAiOauth":{"accessToken":"tok"}}""");
        var callCount = 0;
        var clockStart = DateTimeOffset.UtcNow;
        // First call is "started"; second call inside the loop reports enough elapsed time to trip
        // the budget immediately.
        var discovery = new ClaudeConfigDirDiscovery(
            new InMemoryEnvironment(), files, new InMemoryKeychain(),
            homeDirectory: () => Home,
            listSubdirectories: home => home == Home ? new List<string> { $"{Home}/.claude-a" } : new List<string>(),
            timeBudget: TimeSpan.FromMilliseconds(1),
            now: () =>
            {
                callCount++;
                return callCount == 1 ? clockStart : clockStart.AddSeconds(10);
            });

        var result = discovery.Run();

        Assert.Empty(result.Findings);
        Assert.Contains(result.Notes, n => n.Contains("budget"));
    }
}
