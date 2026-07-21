using AIUsage.Core.Providers.Claude;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

public class DefaultAccountObserverTests
{
    private const string Home = "C:/Users/tester";

    private static DefaultAccountObserver MakeObserver(InMemoryEnvironment env, InMemoryFileSystem files) =>
        new(env, files, homeDirectory: () => Home);

    [Fact]
    public void ObserveClaude_ReturnsAbsent_WhenNoStateFileAndNoCredentials()
    {
        var observer = MakeObserver(new InMemoryEnvironment(), new InMemoryFileSystem());
        var outcome = observer.ObserveClaude();
        Assert.IsType<DefaultAccountObserver.Outcome.Absent>(outcome);
    }

    [Fact]
    public void ObserveClaude_ReturnsUnresolved_WhenCredentialsExistButNoIdentityFile()
    {
        var files = new InMemoryFileSystem().Write($"{Home}/.claude/.credentials.json", "{}");
        var observer = MakeObserver(new InMemoryEnvironment(), files);
        var outcome = observer.ObserveClaude();
        var unresolved = Assert.IsType<DefaultAccountObserver.Outcome.Unresolved>(outcome);
        Assert.Contains("no identity file", unresolved.Reason);
    }

    [Fact]
    public void ObserveClaude_ReturnsResolved_WithIdentityKeyAndLabel()
    {
        var files = new InMemoryFileSystem().Write($"{Home}/.claude.json",
            """{"oauthAccount":{"accountUuid":"UUID-1","emailAddress":"a@b.com","organizationUuid":"ORG-1","organizationName":"Acme"}}""");
        var observer = MakeObserver(new InMemoryEnvironment(), files);

        var outcome = observer.ObserveClaude();

        var resolved = Assert.IsType<DefaultAccountObserver.Outcome.Resolved>(outcome);
        Assert.Equal("uuid-1|org-1", resolved.IdentityKey);
        Assert.Equal("a@b.com (Acme)", resolved.Label);
        Assert.Equal(Home + Path.DirectorySeparatorChar + ".claude", resolved.Anchor);
    }

    [Fact]
    public void ObserveClaude_ReturnsUnresolved_WhenIdentityFileNamesNoAccount()
    {
        var files = new InMemoryFileSystem().Write($"{Home}/.claude.json", "{}");
        var observer = MakeObserver(new InMemoryEnvironment(), files);

        var outcome = observer.ObserveClaude();

        var unresolved = Assert.IsType<DefaultAccountObserver.Outcome.Unresolved>(outcome);
        Assert.Contains("names no account", unresolved.Reason);
    }

    [Fact]
    public void ObserveClaude_ReturnsUnresolved_WhenConfigDirIsCommaSeparatedList()
    {
        var env = new InMemoryEnvironment().Set("CLAUDE_CONFIG_DIR", "~/.claude1,~/.claude2");
        var observer = MakeObserver(env, new InMemoryFileSystem());

        var outcome = observer.ObserveClaude();

        var unresolved = Assert.IsType<DefaultAccountObserver.Outcome.Unresolved>(outcome);
        Assert.Contains("comma-separated", unresolved.Reason);
    }

    [Fact]
    public void ObserveClaude_UsesCustomConfigDir_ReadingIdentityFileInsideIt()
    {
        var env = new InMemoryEnvironment().Set("CLAUDE_CONFIG_DIR", $"{Home}/.claude-work");
        var files = new InMemoryFileSystem().Write($"{Home}/.claude-work/.claude.json",
            """{"oauthAccount":{"accountUuid":"UUID-2"}}""");
        var observer = MakeObserver(env, files);

        var outcome = observer.ObserveClaude();

        var resolved = Assert.IsType<DefaultAccountObserver.Outcome.Resolved>(outcome);
        Assert.Equal("uuid-2", resolved.IdentityKey);
        Assert.Null(resolved.Label);
    }

    [Theory]
    [InlineData("uuid", null, "uuid")]
    [InlineData("uuid", "org", "uuid|org")]
    public void ClaudeIdentityKey_CombinesUuidAndOrg(string uuid, string? org, string expected)
    {
        var account = new DefaultAccountObserver.ClaudeStateFile.OAuthAccountData { AccountUuid = uuid, OrganizationUuid = org };
        Assert.Equal(expected, DefaultAccountObserver.ClaudeIdentityKey(account));
    }

    [Fact]
    public void ClaudeIdentityKey_ReturnsNull_WhenNoUuid()
    {
        var account = new DefaultAccountObserver.ClaudeStateFile.OAuthAccountData();
        Assert.Null(DefaultAccountObserver.ClaudeIdentityKey(account));
    }

    [Theory]
    [InlineData("a@b.com", "Acme", "a@b.com (Acme)")]
    [InlineData("a@b.com", null, "a@b.com")]
    [InlineData(null, "Acme", "Acme")]
    [InlineData(null, null, null)]
    public void ClaudeIdentityLabel_FormatsEmailAndOrg(string? email, string? org, string? expected)
    {
        var account = new DefaultAccountObserver.ClaudeStateFile.OAuthAccountData { EmailAddress = email, OrganizationName = org };
        Assert.Equal(expected, DefaultAccountObserver.ClaudeIdentityLabel(account));
    }
}
