using AIUsage.Core.Providers.Claude;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

/// <summary>Covers the multi-account addition to ClaudeAuthStore: ClaudeCredentialScope routing,
/// the scoped Credential Manager service-name hash (must be byte-identical between discovery-time
/// probing and this store's own read/write — see PORTING_NOTES.md), and that a scoped/ConfigDir card
/// never leaks the ambient CLAUDE_CODE_OAUTH_TOKEN or the CLAUDE_CONFIG_DIR-suffixed keychain probe
/// that belongs only to the Standard card.</summary>
public class ClaudeAuthStoreScopeTests
{
    private const string Home = "C:/Users/tester";

    [Fact]
    public void ScopedKeychainServiceName_IsDeterministic_ForSameLiteral()
    {
        var env = new InMemoryEnvironment();
        var a = ClaudeAuthStore.ScopedKeychainServiceName("/some/config/dir", env);
        var b = ClaudeAuthStore.ScopedKeychainServiceName("/some/config/dir", env);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ScopedKeychainServiceName_DiffersForDifferentLiterals()
    {
        var env = new InMemoryEnvironment();
        var a = ClaudeAuthStore.ScopedKeychainServiceName("/some/config/dir", env);
        var b = ClaudeAuthStore.ScopedKeychainServiceName("/other/config/dir", env);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ScopedKeychainServiceName_MatchesStoresOwnCandidate_ForConfigDirScope()
    {
        var env = new InMemoryEnvironment();
        var scope = new ClaudeCredentialScope.ConfigDir("/some/config/dir", "/some/config/dir");
        var store = new ClaudeAuthStore(environment: env, scope: scope);

        var expected = ClaudeAuthStore.ScopedKeychainServiceName("/some/config/dir", env);
        var candidate = Assert.Single(store.KeychainServiceCandidates());
        Assert.Equal(expected, candidate);
    }

    [Fact]
    public void KeychainServiceCandidates_StandardScope_WithConfigDirOverride_ProbesScopedThenBase()
    {
        var env = new InMemoryEnvironment().Set("CLAUDE_CONFIG_DIR", "~/.claude-work");
        var store = new ClaudeAuthStore(environment: env);

        var candidates = store.KeychainServiceCandidates();

        Assert.Equal(2, candidates.Count);
        Assert.EndsWith(ClaudeAuthStore.ScopedKeychainServiceName("~/.claude-work", env).Split('-').Last(), candidates[0]);
        Assert.Equal(ClaudeAuthStore.BaseKeychainServiceName(env), candidates[1]);
    }

    [Fact]
    public void HasCredentialFootprint_ConfigDirScope_ReadsOnlyItsOwnPath()
    {
        var configDirPath = $"{Home}/.claude-work";
        var files = new InMemoryFileSystem().Write($"{configDirPath}/.credentials.json", "{}");
        var env = new InMemoryEnvironment();
        var scope = new ClaudeCredentialScope.ConfigDir(configDirPath, configDirPath);
        var store = new ClaudeAuthStore(environment: env, files: files, scope: scope);

        Assert.True(store.HasCredentialFootprint());
    }

    [Fact]
    public void HasCredentialFootprint_ConfigDirScope_IgnoresDefaultHomeCredentials()
    {
        var files = new InMemoryFileSystem().Write($"{Home}/.claude/.credentials.json", "{}");
        var env = new InMemoryEnvironment();
        var scope = new ClaudeCredentialScope.ConfigDir($"{Home}/.claude-work", $"{Home}/.claude-work");
        var store = new ClaudeAuthStore(environment: env, files: files, scope: scope);

        Assert.False(store.HasCredentialFootprint());
    }

    [Fact]
    public void LoadCredentialCandidates_ConfigDirScope_NeverAppliesAmbientEnvironmentToken()
    {
        var configDirPath = $"{Home}/.claude-work";
        var files = new InMemoryFileSystem().Write($"{configDirPath}/.credentials.json",
            """{"claudeAiOauth":{"accessToken":"scoped-token"}}""");
        var env = new InMemoryEnvironment().Set("CLAUDE_CODE_OAUTH_TOKEN", "ambient-token");
        var scope = new ClaudeCredentialScope.ConfigDir(configDirPath, configDirPath);
        var store = new ClaudeAuthStore(environment: env, files: files, scope: scope);

        var candidates = store.LoadCredentialCandidates();

        var candidate = Assert.Single(candidates);
        Assert.Equal("scoped-token", candidate.OAuth.AccessToken);
        Assert.False(candidate.InferenceOnly);
    }

    [Fact]
    public void LoadCredentialCandidates_StandardScope_StillAppliesAmbientEnvironmentToken()
    {
        var files = new InMemoryFileSystem();
        var env = new InMemoryEnvironment().Set("CLAUDE_CODE_OAUTH_TOKEN", "ambient-token");
        var store = new ClaudeAuthStore(environment: env, files: files);

        var candidates = store.LoadCredentialCandidates();

        var candidate = Assert.Single(candidates);
        Assert.Equal("ambient-token", candidate.OAuth.AccessToken);
        Assert.True(candidate.InferenceOnly);
    }
}
