using AIUsage.Core.App;
using AIUsage.Core.Providers.Claude;
using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.App;

public class ProviderAccountAssemblyTests
{
    private const string Home = "C:/Users/tester";

    private static DefaultAccountObserver MakeObserver(InMemoryEnvironment env, InMemoryFileSystem files) =>
        new(env, files, homeDirectory: () => Home);

    private static ClaudeConfigDirDiscovery MakeDiscovery(
        InMemoryEnvironment env, InMemoryFileSystem files, InMemoryKeychain keychain, List<string> subdirs) =>
        new(env, files, keychain, homeDirectory: () => Home,
            listSubdirectories: home => home == Home ? subdirs : new List<string>());

    [Fact]
    public void Make_NoDefaultLogin_NoExtraAccounts_ProducesEmptyAssembly()
    {
        var env = new InMemoryEnvironment();
        var files = new InMemoryFileSystem();
        var observer = MakeObserver(env, files);
        var discovery = MakeDiscovery(env, files, new InMemoryKeychain(), new List<string>());
        var accountsStore = new ProviderAccountsStore(new InMemorySettingsStore());

        var assembly = ProviderAccountAssembly.Make(observer, accountsStore, discovery);

        Assert.Empty(assembly.IdentityKeysByCard);
        Assert.Empty(assembly.ClaudeCards);
        Assert.Empty(assembly.DefaultClaudeExtraLogRoots);
    }

    [Fact]
    public void Make_DefaultLoginOnly_ResolvesIdentityButBuildsNoExtraCard()
    {
        var env = new InMemoryEnvironment();
        var files = new InMemoryFileSystem().Write($"{Home}/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-1"}}""");
        var observer = MakeObserver(env, files);
        var discovery = MakeDiscovery(env, files, new InMemoryKeychain(), new List<string>());
        var accountsStore = new ProviderAccountsStore(new InMemorySettingsStore());

        var assembly = ProviderAccountAssembly.Make(observer, accountsStore, discovery);

        Assert.Equal("uuid-1", assembly.IdentityKeysByCard["claude"]);
        Assert.Empty(assembly.ClaudeCards);
    }

    [Fact]
    public void Make_DistinctExtraAccount_BuildsAccountCard()
    {
        var env = new InMemoryEnvironment();
        var files = new InMemoryFileSystem()
            .Write($"{Home}/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-1"}}""")
            .Write($"{Home}/.claude-work/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-2","emailAddress":"b@c.com"}}""")
            .Write($"{Home}/.claude-work/.credentials.json", """{"claudeAiOauth":{"accessToken":"tok"}}""");
        var observer = MakeObserver(env, files);
        var discovery = MakeDiscovery(env, files, new InMemoryKeychain(), new List<string> { $"{Home}/.claude-work" });
        var accountsStore = new ProviderAccountsStore(new InMemorySettingsStore());

        var assembly = ProviderAccountAssembly.Make(observer, accountsStore, discovery);

        var card = Assert.Single(assembly.ClaudeCards);
        Assert.Equal(ProviderAccountID.Make("claude", "uuid-2"), card.Id);
        Assert.Equal($"{Home}/.claude-work", card.ConfigDirPath);
        Assert.Equal("uuid-2", assembly.IdentityKeysByCard[card.Id]);
        Assert.Empty(assembly.DefaultClaudeExtraLogRoots);
    }

    [Fact]
    public void Make_SameAccountExtraConfigDir_FoldsIntoDefaultCard_NoSecondCard()
    {
        var env = new InMemoryEnvironment();
        var files = new InMemoryFileSystem()
            .Write($"{Home}/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-1"}}""")
            .Write($"{Home}/.claude-extra/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-1"}}""")
            .Write($"{Home}/.claude-extra/.credentials.json", """{"claudeAiOauth":{"accessToken":"tok"}}""");
        var observer = MakeObserver(env, files);
        var discovery = MakeDiscovery(env, files, new InMemoryKeychain(), new List<string> { $"{Home}/.claude-extra" });
        var accountsStore = new ProviderAccountsStore(new InMemorySettingsStore());

        var assembly = ProviderAccountAssembly.Make(observer, accountsStore, discovery);

        Assert.Empty(assembly.ClaudeCards);
        Assert.Equal(new List<string> { $"{Home}/.claude-extra" }, assembly.DefaultClaudeExtraLogRoots);
    }

    [Fact]
    public void Make_DefaultLoginUnresolved_SkipsExtraAccountDiscoveryEntirely()
    {
        var env = new InMemoryEnvironment();
        // Credentials exist at the default home but no identity file -> unresolved.
        var files = new InMemoryFileSystem()
            .Write($"{Home}/.claude/.credentials.json", "{}")
            .Write($"{Home}/.claude-work/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-2"}}""")
            .Write($"{Home}/.claude-work/.credentials.json", """{"claudeAiOauth":{"accessToken":"tok"}}""");
        var observer = MakeObserver(env, files);
        var discovery = MakeDiscovery(env, files, new InMemoryKeychain(), new List<string> { $"{Home}/.claude-work" });
        var accountsStore = new ProviderAccountsStore(new InMemorySettingsStore());

        var assembly = ProviderAccountAssembly.Make(observer, accountsStore, discovery);

        Assert.Empty(assembly.ClaudeCards);
        Assert.DoesNotContain("claude", assembly.IdentityKeysByCard.Keys);
    }

    [Fact]
    public void Make_ReconcilesAcrossLaunches_KeepsStableCardId()
    {
        var env = new InMemoryEnvironment();
        var files = new InMemoryFileSystem()
            .Write($"{Home}/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-1"}}""")
            .Write($"{Home}/.claude-work/.claude.json", """{"oauthAccount":{"accountUuid":"UUID-2"}}""")
            .Write($"{Home}/.claude-work/.credentials.json", """{"claudeAiOauth":{"accessToken":"tok"}}""");
        var observer = MakeObserver(env, files);
        var discovery = MakeDiscovery(env, files, new InMemoryKeychain(), new List<string> { $"{Home}/.claude-work" });
        var settings = new InMemorySettingsStore();

        var firstLaunchCardId = ProviderAccountAssembly.Make(observer, new ProviderAccountsStore(settings), discovery)
            .ClaudeCards.Single().Id;
        var secondLaunchCardId = ProviderAccountAssembly.Make(observer, new ProviderAccountsStore(settings), discovery)
            .ClaudeCards.Single().Id;

        Assert.Equal(firstLaunchCardId, secondLaunchCardId);
    }
}
