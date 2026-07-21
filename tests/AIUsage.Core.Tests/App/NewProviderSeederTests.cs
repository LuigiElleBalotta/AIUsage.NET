using AIUsage.Core.App;
using AIUsage.Core.Providers;
using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;

namespace AIUsage.Core.Tests.App;

public class NewProviderSeederTests
{
    private static ProviderEnablementStore MakeEnablementSeededWith(IEnumerable<string> knownIds, IEnumerable<string>? enabledIds = null)
    {
        var store = new ProviderEnablementStore(new InMemorySettingsStore());
        store.RegisterKnownProviders(knownIds);
        store.SeedEnabledProviders(enabledIds ?? knownIds);
        return store;
    }

    [Fact]
    public void ReconcileIfNeeded_NeverSeeded_ReturnsNull()
    {
        var enablement = new ProviderEnablementStore(new InMemorySettingsStore());
        var providers = new List<IProviderRuntime> { new FakeProviderRuntime("claude") };

        var task = NewProviderSeeder.ReconcileIfNeeded(providers, enablement);

        Assert.Null(task);
    }

    [Fact]
    public void ReconcileIfNeeded_NoNewProviders_ReturnsNull()
    {
        var enablement = MakeEnablementSeededWith(new[] { "claude", "codex" });
        var providers = new List<IProviderRuntime> { new FakeProviderRuntime("claude"), new FakeProviderRuntime("codex") };

        var task = NewProviderSeeder.ReconcileIfNeeded(providers, enablement);

        Assert.Null(task);
    }

    [Fact]
    public async Task ReconcileIfNeeded_NewProviderWithCredentials_GetsEnabled()
    {
        var enablement = MakeEnablementSeededWith(new[] { "claude" });
        var newProvider = new FakeProviderRuntime("grok", hasLocalCredentials: true);
        var providers = new List<IProviderRuntime> { new FakeProviderRuntime("claude"), newProvider };

        var task = NewProviderSeeder.ReconcileIfNeeded(providers, enablement);
        Assert.NotNull(task);
        await task!;

        Assert.True(enablement.IsEnabled("grok"));
        Assert.Equal(1, newProvider.HasLocalCredentialsCallCount);
    }

    [Fact]
    public async Task ReconcileIfNeeded_NewProviderWithoutCredentials_StaysDisabled()
    {
        var enablement = MakeEnablementSeededWith(new[] { "claude" });
        var providers = new List<IProviderRuntime> { new FakeProviderRuntime("claude"), new FakeProviderRuntime("grok", hasLocalCredentials: false) };

        var task = NewProviderSeeder.ReconcileIfNeeded(providers, enablement);
        Assert.NotNull(task);
        await task!;

        Assert.False(enablement.IsEnabled("grok"));
    }

    [Fact]
    public async Task ReconcileIfNeeded_UserAlreadyEnabledNewProvider_IsNotTouchedAgain()
    {
        var enablement = MakeEnablementSeededWith(new[] { "claude" });
        var newProvider = new FakeProviderRuntime("grok", hasLocalCredentials: true);
        var providers = new List<IProviderRuntime> { new FakeProviderRuntime("claude"), newProvider };

        // Simulate the user manually enabling "grok" themselves before the probe resolves.
        enablement.SetEnabled(true, "grok");

        var task = NewProviderSeeder.ReconcileIfNeeded(providers, enablement);
        Assert.NotNull(task);
        await task!;

        Assert.True(enablement.IsEnabled("grok"));
    }

    [Fact]
    public void ReconcileIfNeeded_KnownProvidersUntouched_NeverProbed()
    {
        var enablement = MakeEnablementSeededWith(new[] { "claude", "codex" }, enabledIds: new[] { "claude" });
        var claude = new FakeProviderRuntime("claude");
        var codex = new FakeProviderRuntime("codex");
        var providers = new List<IProviderRuntime> { claude, codex };

        var task = NewProviderSeeder.ReconcileIfNeeded(providers, enablement);

        Assert.Null(task);
        Assert.Equal(0, claude.HasLocalCredentialsCallCount);
        Assert.Equal(0, codex.HasLocalCredentialsCallCount);
        // codex was previously known but disabled by the user - must remain disabled, untouched.
        Assert.False(enablement.IsEnabled("codex"));
    }
}
