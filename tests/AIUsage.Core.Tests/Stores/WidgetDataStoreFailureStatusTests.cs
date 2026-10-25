using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Stores;

/// <summary>
/// Regression coverage for the shared provider-failure presentation gap: a fatal refresh error must
/// be visible on the card even before any snapshot exists, and retained last-good data must be
/// labelled as last successful data rather than presented as current.
/// </summary>
public class WidgetDataStoreFailureStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Provider OpenCode = new("opencode", "OpenCode", "opencode");
    private const string PartialCostSubtitle =
        "Some OpenCode messages have no recorded cost. Dollar figures are known subtotals; missing costs aren't guessed.";

    private static List<WidgetDescriptor> OpenCodeDescriptors => new OpenCodeProvider().WidgetDescriptors;

    private static WidgetDescriptor TodayDescriptor() =>
        OpenCodeDescriptors.Single(d => d.Id == "opencode.today");

    private static WidgetDescriptor SessionDescriptor() =>
        WidgetDescriptorFactories.Percent("opencode.session", OpenCode, "Session").ShowingRemainingPercent();

    private static ProviderSnapshot GoodSnapshot(DateTimeOffset? refreshedAt = null) =>
        new("opencode", "OpenCode", "Go",
            new List<MetricLine>
            {
                new MetricLine.Progress("Session", 0.72, 12, ProgressFormat.DollarsValue)
            },
            refreshedAt ?? Now,
            DataVersion: OpenCodeQuotaCompatibility.CurrentDataVersion);

    private static ProviderSnapshot PartialCostSnapshot(DateTimeOffset? refreshedAt = null) =>
        new("opencode", "OpenCode", "Go",
            new List<MetricLine>
            {
                new MetricLine.Progress("Session", 0.72, 12, ProgressFormat.DollarsValue),
                new MetricLine.Badge("Local Usage", "Partial Cost", "#F59E0B", PartialCostSubtitle),
                new MetricLine.Badge("Local Usage", "Partial Cost", "#F59E0B", PartialCostSubtitle),
                new MetricLine.Values("Today", new List<MetricValue>
                {
                    new(0.80, MetricKind.Dollars, Estimated: false),
                    new(1500, MetricKind.Count, "tokens")
                })
            },
            refreshedAt ?? Now,
            DataVersion: OpenCodeQuotaCompatibility.CurrentDataVersion);

    private static ProviderSnapshot FullKnownSnapshot(DateTimeOffset? refreshedAt = null) =>
        new("opencode", "OpenCode", "Go",
            new List<MetricLine>
            {
                new MetricLine.Progress("Session", 0.72, 12, ProgressFormat.DollarsValue),
                new MetricLine.Values("Today", new List<MetricValue>
                {
                    new(1.25, MetricKind.Dollars, Estimated: false),
                    new(2400, MetricKind.Count, "tokens")
                })
            },
            refreshedAt ?? Now,
            DataVersion: OpenCodeQuotaCompatibility.CurrentDataVersion);

    private static ProviderSnapshot NotLoggedInSnapshot(DateTimeOffset? refreshedAt = null) =>
        ProviderSnapshot.Error(OpenCode, new OpenCodeUsageError(OpenCodeUsageErrorKind.NotLoggedIn),
            refreshedAt ?? Now);

    private static (WidgetDataStore Store, ScriptedProviderRuntime Runtime) CreateStore(
        ProviderSnapshot? cached,
        IReadOnlyList<WidgetDescriptor>? descriptors,
        params ProviderSnapshot[] refreshResults)
    {
        var descriptorList = descriptors?.ToList() ?? new List<WidgetDescriptor> { SessionDescriptor() };
        var runtime = new ScriptedProviderRuntime(OpenCode, refreshResults)
        {
            WidgetDescriptors = descriptorList
        };
        var registry = new WidgetRegistry(new List<Provider> { OpenCode }, runtime.WidgetDescriptors);
        var cache = new ProviderSnapshotCache(new InMemorySettingsStore(), now: () => Now);
        if (cached is not null) cache.Store(cached);
        var store = new WidgetDataStore(registry, new List<IProviderRuntime> { runtime }, cache, now: () => Now);
        return (store, runtime);
    }

    [Fact]
    public async Task CachedGoodData_ThenFatalNotLoggedIn_RetainsValueAndShowsLastSuccessfulFailureStatus()
    {
        var (store, _) = CreateStore(GoodSnapshot(), null, NotLoggedInSnapshot(Now.AddMinutes(5)));

        var outcome = await store.RefreshAsync("opencode", force: true);

        Assert.Equal(RefreshOutcome.Failed, outcome);
        Assert.Equal("94% left", store.Data(SessionDescriptor()).BoundedHeadline);
        Assert.True(store.Snapshots.ContainsKey("opencode"));
        Assert.True(store.ProviderErrors.ContainsKey("opencode"));

        var status = Assert.Single(store.StatusBadges("opencode"));
        Assert.Equal("Status", status.Title);
        Assert.Equal("Refresh Failed", status.Headline);
        Assert.Contains("OpenCode not detected", status.UnboundedDetail, StringComparison.Ordinal);
        Assert.Contains("last successful data", status.UnboundedDetail, StringComparison.Ordinal);
        Assert.Equal(WidgetData.MeterSeverity.Critical, status.GetMeterState(Now).Severity);
    }

    [Fact]
    public async Task FirstEverFatalWithoutSnapshot_ShowsFriendlyErrorAndNoInventedValue()
    {
        var (store, _) = CreateStore(null, null, NotLoggedInSnapshot());

        await store.RefreshAsync("opencode", force: true);

        Assert.False(store.Snapshots.ContainsKey("opencode"));
        Assert.False(store.Data(SessionDescriptor()).HasData);

        var status = Assert.Single(store.StatusBadges("opencode"));
        Assert.Equal("Refresh Failed", status.Headline);
        Assert.Equal("OpenCode not detected. Log in with OpenCode Go or use OpenCode locally first.",
            status.UnboundedDetail);
        Assert.DoesNotContain("last successful data", status.UnboundedDetail, StringComparison.Ordinal);
        Assert.Equal(WidgetData.MeterSeverity.Critical, status.GetMeterState(Now).Severity);
    }

    [Fact]
    public async Task SuccessfulRecovery_ClearsFailureStatusAndShowsFreshValue()
    {
        var (store, runtime) = CreateStore(null, null, NotLoggedInSnapshot(), GoodSnapshot(Now.AddMinutes(5)));

        await store.RefreshAsync("opencode", force: true);
        Assert.Single(store.StatusBadges("opencode"));

        var outcome = await store.RefreshAsync("opencode", force: true);

        Assert.Equal(RefreshOutcome.Refreshed, outcome);
        Assert.Equal(2, runtime.RefreshCount);
        Assert.Empty(store.ProviderErrors);
        Assert.Empty(store.StatusBadges("opencode"));
        Assert.Equal("94% left", store.Data(SessionDescriptor()).BoundedHeadline);
    }

    [Fact]
    public async Task CachedPartialCostData_ThenFatalFailure_KeepsQualifierAndKnownSubtotalAfterFailure()
    {
        var (store, _) = CreateStore(
            PartialCostSnapshot(), OpenCodeDescriptors, NotLoggedInSnapshot(Now.AddMinutes(5)));

        // The duplicate warning line is deduplicated before the failure.
        var before = Assert.Single(store.StatusBadges("opencode"));
        Assert.Equal("Partial Cost", before.Headline);

        var outcome = await store.RefreshAsync("opencode", force: true);

        Assert.Equal(RefreshOutcome.Failed, outcome);

        var badges = store.StatusBadges("opencode");
        Assert.Equal(2, badges.Count);

        Assert.Equal("Status", badges[0].Title);
        Assert.Equal("Refresh Failed", badges[0].Headline);
        Assert.Contains("OpenCode not detected", badges[0].UnboundedDetail, StringComparison.Ordinal);
        Assert.Contains("last successful data", badges[0].UnboundedDetail, StringComparison.Ordinal);
        Assert.Equal(WidgetData.MeterSeverity.Critical, badges[0].GetMeterState(Now).Severity);

        // The retained snapshot's qualifier keeps its label, text, and warning severity.
        Assert.Equal("Local Usage", badges[1].Title);
        Assert.Equal("Partial Cost", badges[1].Headline);
        Assert.Equal(PartialCostSubtitle, badges[1].UnboundedDetail);
        Assert.Equal(WidgetData.MeterSeverity.Warning, badges[1].GetMeterState(Now).Severity);
        Assert.Single(badges, b => b.Headline == "Refresh Failed");
        Assert.Single(badges, b => b.Headline == "Partial Cost");

        // The retained known subtotal is unchanged: missing costs are still never invented.
        var today = store.Data(TodayDescriptor());
        Assert.True(today.HasData);
        Assert.Equal("$0.80", today.Headline);
        Assert.Contains("1.5K tokens", today.UnboundedDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CachedPartialCostData_ThenFatalFailure_ThenFullKnownRecovery_RemovesFailureAndStaleQualifier()
    {
        var (store, _) = CreateStore(
            PartialCostSnapshot(), OpenCodeDescriptors,
            NotLoggedInSnapshot(Now.AddMinutes(5)), FullKnownSnapshot(Now.AddMinutes(10)));

        Assert.Equal(RefreshOutcome.Failed, await store.RefreshAsync("opencode", force: true));
        Assert.Contains(store.StatusBadges("opencode"), b => b.Headline == "Partial Cost");

        var outcome = await store.RefreshAsync("opencode", force: true);

        Assert.Equal(RefreshOutcome.Refreshed, outcome);
        Assert.Empty(store.ProviderErrors);
        Assert.Empty(store.StatusBadges("opencode"));

        var today = store.Data(TodayDescriptor());
        Assert.True(today.HasData);
        Assert.Equal("$1.25", today.Headline);
        Assert.Contains("2.4K tokens", today.UnboundedDetail, StringComparison.Ordinal);
    }
}
