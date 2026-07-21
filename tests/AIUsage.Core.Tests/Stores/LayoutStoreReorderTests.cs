using AIUsage.Core.Models;
using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;

namespace AIUsage.Core.Tests.Stores;

public class LayoutStoreReorderTests
{
    private static (LayoutStore Store, WidgetRegistry Registry) MakeStore(HashSet<string>? disabledProviders = null)
    {
        var claude = new Provider("claude", "Claude", "claude");
        var codex = new Provider("codex", "Codex", "codex");
        var cursor = new Provider("cursor", "Cursor", "cursor");

        var descriptors = new List<WidgetDescriptor>
        {
            WidgetDescriptorFactories.Percent("claude.session", claude, "Session"),
            WidgetDescriptorFactories.Percent("claude.weekly", claude, "Weekly"),
            WidgetDescriptorFactories.Percent("codex.session", codex, "Session"),
            WidgetDescriptorFactories.Percent("codex.weekly", codex, "Weekly"),
            WidgetDescriptorFactories.Percent("cursor.usage", cursor, "Total Usage"),
        };
        var registry = new WidgetRegistry(new List<Provider> { claude, codex, cursor }, descriptors);
        var disabled = disabledProviders ?? new HashSet<string>();
        var store = new LayoutStore(registry, new InMemorySettingsStore(), isProviderEnabled: id => !disabled.Contains(id));
        return (store, registry);
    }

    [Fact]
    public void Reordered_MovesDraggedAdjacentToTarget_WhenMovingDown()
    {
        var result = LayoutStore.Reordered(new List<string> { "a", "b", "c", "d" }, "a", "c");
        Assert.Equal(new List<string> { "b", "c", "a", "d" }, result);
    }

    [Fact]
    public void Reordered_MovesDraggedAdjacentToTarget_WhenMovingUp()
    {
        var result = LayoutStore.Reordered(new List<string> { "a", "b", "c", "d" }, "d", "b");
        Assert.Equal(new List<string> { "a", "d", "b", "c" }, result);
    }

    [Fact]
    public void Reordered_SameDraggedAndTarget_ReturnsNull()
    {
        Assert.Null(LayoutStore.Reordered(new List<string> { "a", "b" }, "a", "a"));
    }

    [Fact]
    public void Reordered_MissingId_ReturnsNull()
    {
        Assert.Null(LayoutStore.Reordered(new List<string> { "a", "b" }, "a", "z"));
        Assert.Null(LayoutStore.Reordered(new List<string> { "a", "b" }, "z", "a"));
    }

    [Fact]
    public void ReorderProvider_MovesProviderAndPersists()
    {
        var (store, _) = MakeStore();

        var changed = store.ReorderProvider("cursor", "claude");

        Assert.True(changed);
        Assert.Equal(new List<string> { "cursor", "claude", "codex" }, store.OrderedProviderIds());
    }

    [Fact]
    public void ReorderProvider_DisabledProviderKeepsItsPosition()
    {
        var (store, _) = MakeStore(disabledProviders: new HashSet<string> { "codex" });

        // Only claude/cursor are "shown" (enabled). Reordering substitutes into their existing slots
        // in the raw sequence [claude, codex, cursor] positionally, so codex (disabled) keeps slot 1
        // while the visible ids (claude, cursor) swap between slots 0 and 2.
        var changed = store.ReorderProvider("cursor", "claude");

        Assert.True(changed);
        Assert.Equal(new List<string> { "cursor", "codex", "claude" }, store.ProviderOrder);
    }

    [Fact]
    public void ReorderProvider_SameProvider_ReturnsFalse()
    {
        var (store, _) = MakeStore();
        Assert.False(store.ReorderProvider("claude", "claude"));
    }

    [Fact]
    public void ReorderProvider_PersistsAcrossNewInstance()
    {
        var settings = new InMemorySettingsStore();
        var claude = new Provider("claude", "Claude", "claude");
        var codex = new Provider("codex", "Codex", "codex");
        var descriptors = new List<WidgetDescriptor>
        {
            WidgetDescriptorFactories.Percent("claude.session", claude, "Session"),
            WidgetDescriptorFactories.Percent("codex.session", codex, "Session"),
        };
        var registry = new WidgetRegistry(new List<Provider> { claude, codex }, descriptors);
        var store1 = new LayoutStore(registry, settings);
        store1.ReorderProvider("codex", "claude");

        var store2 = new LayoutStore(registry, settings);
        Assert.Equal(new List<string> { "codex", "claude" }, store2.OrderedProviderIds());
    }

    [Fact]
    public void ReorderMetric_MovesMetricWithinProvider()
    {
        var (store, _) = MakeStore();

        var changed = store.ReorderMetric("claude.weekly", "claude.session", "claude");

        Assert.True(changed);
        Assert.Equal(new List<string> { "claude.weekly", "claude.session" }, store.MetricOrder("claude"));
    }

    [Fact]
    public void ReorderMetric_SameMetric_ReturnsFalse()
    {
        var (store, _) = MakeStore();
        Assert.False(store.ReorderMetric("claude.session", "claude.session", "claude"));
    }

    [Fact]
    public void ReorderMetric_TargetInDifferentProvider_ReturnsFalse()
    {
        var (store, _) = MakeStore();
        Assert.False(store.ReorderMetric("claude.session", "codex.session", "claude"));
    }

    [Fact]
    public void ReorderMetric_PersistsAndSyncsPlacedOrder()
    {
        var (store, _) = MakeStore();

        store.ReorderMetric("claude.weekly", "claude.session", "claude");

        // Placed contains every descriptor (nothing was explicitly disabled), so claude's two metrics
        // should now appear weekly-then-session at the front, ahead of every other provider's rows.
        var placedIds = store.Placed.Select(w => w.DescriptorId).ToList();
        Assert.Equal(new List<string> { "claude.weekly", "claude.session" }, placedIds.Take(2));
    }
}
