using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Stores;

/// <summary>
/// Presentation-layer regression coverage for OpenCode's server quota: the three Go meters display
/// percent left, while the raw provider lines and the machine-facing limits API stay in USD.
/// </summary>
public class WidgetDataStoreOpenCodePresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Provider OpenCode = new("opencode", "OpenCode", "opencode");

    private static List<WidgetDescriptor> OpenCodeDescriptors => new OpenCodeProvider().WidgetDescriptors;

    private static WidgetDescriptor Descriptor(string id) =>
        OpenCodeDescriptors.Single(d => d.Id == id);

    private static ProviderSnapshot Snapshot(params MetricLine[] lines) =>
        new("opencode", "OpenCode", "Go", lines.ToList(), Now,
            DataVersion: OpenCodeQuotaCompatibility.CurrentDataVersion);

    private static WidgetDataStore StoreWith(ProviderSnapshot snapshot, params WidgetDescriptor[] descriptors)
    {
        var provider = new Provider(snapshot.ProviderId, snapshot.DisplayName, snapshot.ProviderId);
        var registry = new WidgetRegistry(new List<Provider> { provider }, descriptors.ToList());
        var cache = new ProviderSnapshotCache(new InMemorySettingsStore(), now: () => Now);
        cache.Store(snapshot);
        return new WidgetDataStore(registry, new List<IProviderRuntime>(), cache, now: () => Now);
    }

    private static MetricLine.Progress Dollars(string label, double used, double limit, DateTimeOffset? resetsAt = null, long? periodMs = null) =>
        new(label, used, limit, ProgressFormat.DollarsValue, resetsAt, periodMs);

    [Fact]
    public void SessionWeeklyMonthly_DisplayRoundedPercentLeft()
    {
        var snapshot = Snapshot(
            Dollars("Session", 0.72, 12),   // 6% used
            Dollars("Weekly", 5.7, 30),     // 19% used
            Dollars("Monthly", 5.4, 60));   // 9% used
        var store = StoreWith(snapshot, OpenCodeDescriptors.ToArray());

        var session = store.Data(Descriptor("opencode.session"));
        var weekly = store.Data(Descriptor("opencode.weekly"));
        var monthly = store.Data(Descriptor("opencode.monthly"));

        Assert.Equal("94% left", session.BoundedHeadline);
        Assert.Equal("81% left", weekly.BoundedHeadline);
        Assert.Equal("91% left", monthly.BoundedHeadline);
        Assert.Equal("94%", session.MenuBarValue);
        Assert.Equal(MetricKind.Percent, session.Kind);
        Assert.Equal(100, session.Limit);
        Assert.Equal(WidgetDisplayMode.Remaining, session.DisplayMode);
        Assert.Equal(0.94, session.RemainingFraction, precision: 6);
        Assert.True(session.HasData);
    }

    [Fact]
    public void CurrentMonth_9_56PercentUsed_RoundsTo90PercentLeft()
    {
        // The live sample at the time of this change: $5.736 of the $60 monthly quota used.
        var snapshot = Snapshot(Dollars("Monthly", 5.736, 60));
        var store = StoreWith(snapshot, Descriptor("opencode.monthly"));

        var monthly = store.Data(Descriptor("opencode.monthly"));

        Assert.Equal(9.56, monthly.Used, precision: 6);
        Assert.Equal("90%", monthly.ValueText);
        Assert.Equal("90% left", monthly.BoundedHeadline);
    }

    [Fact]
    public void PercentPresentation_DoesNotChangeProviderLineOrLimitsApiUnits()
    {
        var snapshot = Snapshot(
            Dollars("Session", 0.72, 12),
            Dollars("Weekly", 5.7, 30),
            Dollars("Monthly", 5.736, 60));
        var descriptors = OpenCodeDescriptors.ToArray();
        var state = new LocalUsageApi.State(
            EnabledOrderedIds: new List<string> { "opencode" },
            KnownIds: new HashSet<string> { "opencode" },
            Snapshots: new Dictionary<string, ProviderSnapshot> { ["opencode"] = snapshot },
            LimitDescriptors: new Dictionary<string, List<WidgetDescriptor>> { ["opencode"] = descriptors.ToList() });

        var json = System.Text.Json.JsonDocument.Parse(LocalLimitsApi.Encode(new List<string> { "opencode" }, state)).RootElement;
        var resources = json.GetProperty("providers").GetProperty("opencode").GetProperty("resources");

        var session = resources.GetProperty("session");
        Assert.Equal("usd", session.GetProperty("unit").GetString());
        Assert.Equal(12, session.GetProperty("limit").GetDouble());
        Assert.Equal(0.72, session.GetProperty("used").GetDouble(), precision: 6);
        Assert.Equal(11.28, session.GetProperty("remaining").GetDouble(), precision: 6);

        var monthly = resources.GetProperty("monthly");
        Assert.Equal("usd", monthly.GetProperty("unit").GetString());
        Assert.Equal(60, monthly.GetProperty("limit").GetDouble());
        Assert.Equal(5.736, monthly.GetProperty("used").GetDouble(), precision: 6);
        Assert.Equal(0.0956, monthly.GetProperty("utilization").GetDouble(), precision: 6);

        // Resolving WidgetData for display must not rewrite the authoritative provider lines.
        var sessionLine = Assert.IsType<MetricLine.Progress>(snapshot.Line("Session"));
        Assert.IsType<ProgressFormat.Dollars>(sessionLine.Format);
        Assert.Equal(0.72, sessionLine.Used, precision: 6);
        Assert.Equal(12, sessionLine.Limit, precision: 6);
    }

    [Fact]
    public void NoData_FallsBackToPercentSampleWithoutInventingAValue()
    {
        var store = StoreWith(Snapshot(), Descriptor("opencode.session"));

        var session = store.Data(Descriptor("opencode.session"));

        Assert.False(session.HasData);
        Assert.Equal(MetricKind.Percent, session.Kind);
        Assert.Equal(100, session.Limit);
        Assert.Equal("\u2014", session.ValueText);
        Assert.Equal("\u2014", session.Headline);
        Assert.Null(session.BoundedSubtitle);
        Assert.Equal(0, session.Fraction);
        Assert.Equal(0, session.RemainingFraction);
        Assert.Equal(WidgetDisplayMode.Remaining, session.DisplayMode);
    }

    [Fact]
    public void GlobalUsedStyle_IsOverriddenOnlyForOptedInWidgets()
    {
        var other = WidgetDescriptorFactories.BoundedDollars("opencode.other", OpenCode, "Other", 100);
        var today = Descriptor("opencode.today");
        var snapshot = Snapshot(
            Dollars("Session", 0.72, 12),
            Dollars("Other", 42, 100),
            new MetricLine.Values("Today", new List<MetricValue> { new(5.25, MetricKind.Dollars) }));
        var store = StoreWith(snapshot, Descriptor("opencode.session"), other, today);
        store.MeterStyle = WidgetDisplayMode.Used;

        var session = store.Data(Descriptor("opencode.session"));
        var otherData = store.Data(other);
        var todayData = store.Data(today);

        Assert.Equal(WidgetDisplayMode.Remaining, session.DisplayMode);
        Assert.Equal("94% left", session.BoundedHeadline);

        Assert.Equal(WidgetDisplayMode.Used, otherData.DisplayMode);
        Assert.Equal(MetricKind.Dollars, otherData.Kind);
        Assert.Equal("$42.00", otherData.ValueText);
        Assert.Equal(100, otherData.Limit);

        Assert.Equal(WidgetDisplayMode.Used, todayData.DisplayMode);
        Assert.Equal(MetricKind.Dollars, todayData.Kind);
        Assert.True(todayData.HasData);
    }

    [Fact]
    public void CompleteAndExceededLimits_ShowZeroPercentLeft()
    {
        var snapshot = Snapshot(
            Dollars("Session", 12, 12),
            Dollars("Monthly", 61.2, 60)); // 102% used
        var store = StoreWith(snapshot, Descriptor("opencode.session"), Descriptor("opencode.monthly"));

        var session = store.Data(Descriptor("opencode.session"));
        var monthly = store.Data(Descriptor("opencode.monthly"));

        Assert.Equal("0%", session.ValueText);
        Assert.Equal("0% left", session.BoundedHeadline);
        Assert.Equal(0, session.RemainingFraction);

        Assert.Equal("0%", monthly.ValueText);
        Assert.Equal("0% left", monthly.BoundedHeadline);
        Assert.Equal(0, monthly.RemainingFraction);
        Assert.Equal(102, monthly.Used, precision: 6); // overage ratio is preserved, not clamped to used=100
        Assert.IsType<WidgetData.MeterState.Spent>(monthly.GetMeterState(Now));
    }

    [Fact]
    public void PercentPresentation_PreservesResetAndPacing()
    {
        var resetsAt = Now.AddDays(2);
        const long periodMs = 7 * 24 * 60 * 60 * 1000;
        var raw = WidgetDescriptorFactories.BoundedDollars("opencode.weekly", OpenCode, "Weekly", 0)
            .ExportingLimit("weekly", unit: "usd");
        var converted = raw.ShowingRemainingPercent();
        var snapshot = Snapshot(Dollars("Weekly", 5.7, 30, resetsAt, periodMs));
        var store = StoreWith(snapshot, raw, converted);

        var rawData = store.Data(raw);
        var convertedData = store.Data(converted);

        Assert.Equal(resetsAt, convertedData.ResetsAt);
        Assert.Equal(periodMs, convertedData.PeriodDurationMs);
        Assert.Equal(rawData.RemainingFraction, convertedData.RemainingFraction, precision: 6);
        Assert.Equal(rawData.GetMeterState(Now).Severity, convertedData.GetMeterState(Now).Severity);
        Assert.Equal(rawData.GetMeterState(Now).GetType(), convertedData.GetMeterState(Now).GetType());
    }

    [Fact]
    public void OnlyTheThreeGoMeters_OptIntoRemainingPercent()
    {
        var optedIn = OpenCodeDescriptors
            .Where(d => d.ValuePresentation == WidgetValuePresentation.RemainingPercent)
            .Select(d => d.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { "opencode.monthly", "opencode.session", "opencode.weekly" },
            optedIn);
    }

    [Theory]
    [InlineData("Reconnect", "#F59E0B", "OpenCode sign-in expired. Run OpenCode to reconnect. Local usage is still shown.", WidgetData.MeterSeverity.Warning)]
    [InlineData("Reconnect", "#EF4444", "OpenCode Console rejected the saved sign-in. Run OpenCode to reconnect. Local usage is still shown.", WidgetData.MeterSeverity.Critical)]
    [InlineData("Unavailable", "#F59E0B", "Couldn't reach OpenCode Console. Local usage is still shown.", WidgetData.MeterSeverity.Warning)]
    public void QuotaStatusBadge_IsVisibleThroughDataStoreWhilePreservingLocalSpendAndNoInventedQuota(
        string badgeText,
        string colorHex,
        string subtitle,
        WidgetData.MeterSeverity expectedSeverity)
    {
        var snapshot = Snapshot(
            OpenCodeUsageMapper.QuotaBadge(badgeText, colorHex, subtitle),
            new MetricLine.Values("Today", new List<MetricValue>
            {
                new(1.25, MetricKind.Dollars, Estimated: false),
                new(2400, MetricKind.Count, "tokens")
            }),
            new MetricLine.Values("Last 30 Days", new List<MetricValue>
            {
                new(14.50, MetricKind.Dollars, Estimated: false),
                new(48000, MetricKind.Count, "tokens")
            }),
            new MetricLine.Chart("Usage Trend", new List<MetricChartPoint>
            {
                new(2400, "Oct 22", "2.4K tokens")
            }, OpenCodeUsageMapper.QuotaSourceNote));
        var store = StoreWith(snapshot, OpenCodeDescriptors.ToArray());

        var badges = store.StatusBadges("opencode");
        var quotaBadge = Assert.Single(badges);
        Assert.Equal("Go Quota", quotaBadge.Title);
        Assert.Equal(badgeText, quotaBadge.Headline);
        Assert.Equal(subtitle, quotaBadge.UnboundedDetail);
        Assert.False(quotaBadge.IsBounded);
        Assert.True(quotaBadge.HasData);
        Assert.Equal(expectedSeverity, quotaBadge.GetMeterState(Now).Severity);

        foreach (var id in new[] { "opencode.session", "opencode.weekly", "opencode.monthly" })
        {
            var meter = store.Data(Descriptor(id));
            Assert.False(meter.HasData);
            Assert.Equal("\u2014", meter.Headline);
            Assert.Null(meter.BoundedSubtitle);
            Assert.Equal(0, meter.Fraction);
            Assert.Equal(0, meter.RemainingFraction);
        }

        var today = store.Data(Descriptor("opencode.today"));
        var last30 = store.Data(Descriptor("opencode.last30"));
        var trend = store.Data(Descriptor("opencode.trend"));
        Assert.True(today.HasData);
        Assert.Equal("$1.25", today.Headline);
        Assert.Contains("2.4K tokens", today.UnboundedDetail, StringComparison.Ordinal);
        Assert.True(last30.HasData);
        Assert.Equal("$14.50", last30.Headline);
        Assert.True(trend.HasData);
        Assert.Single(trend.ChartPoints);
    }

    [Fact]
    public void LocalScannerFailure_SurfacesLocalUsageBadgeWhilePreservingServerQuotas()
    {
        var snapshot = Snapshot(
            Dollars("Session", 0.72, 12),
            Dollars("Weekly", 5.7, 30),
            Dollars("Monthly", 5.4, 60),
            OpenCodeUsageMapper.LocalUsageBadge(
                "Unavailable",
                "#EF4444",
                "Couldn't read OpenCode's local database. Quit OpenCode and refresh, or check the data directory's permissions. Server quota is still shown."));
        var store = StoreWith(snapshot, OpenCodeDescriptors.ToArray());

        var badge = Assert.Single(store.StatusBadges("opencode"));
        Assert.Equal("Local Usage", badge.Title);
        Assert.Equal("Unavailable", badge.Headline);
        Assert.Contains("Server quota is still shown.", badge.UnboundedDetail, StringComparison.Ordinal);
        Assert.Equal(WidgetData.MeterSeverity.Critical, badge.GetMeterState(Now).Severity);
        Assert.False(badge.IsBounded);

        Assert.Equal("94% left", store.Data(Descriptor("opencode.session")).BoundedHeadline);
        Assert.Equal("81% left", store.Data(Descriptor("opencode.weekly")).BoundedHeadline);
        Assert.Equal("91% left", store.Data(Descriptor("opencode.monthly")).BoundedHeadline);
        Assert.False(store.Data(Descriptor("opencode.today")).HasData);
        Assert.False(store.Data(Descriptor("opencode.last30")).HasData);
    }

    [Fact]
    public void PartialAndUnsupportedSchemaBadges_RemainVisibleAlongsideReadableLocalSpendWithoutDuplication()
    {
        var unsupported = OpenCodeUsageMapper.LocalUsageBadge(
            "Unsupported Schema",
            "#EF4444",
            "OpenCode's local database format isn't recognized. Update OpenCode or AIUsage.NET.");
        var partial = OpenCodeUsageMapper.LocalUsageBadge(
            "Partial Data",
            "#F59E0B",
            "Some OpenCode databases couldn't be read this refresh.");
        var snapshot = Snapshot(
            Dollars("Session", 0.72, 12),
            unsupported,
            partial,
            partial, // duplicate line should be deduplicated by the presentation bridge
            new MetricLine.Values("Today", new List<MetricValue>
            {
                new(0.80, MetricKind.Dollars, Estimated: false),
                new(1500, MetricKind.Count, "tokens")
            }),
            new MetricLine.Values("Last 30 Days", new List<MetricValue>
            {
                new(0.80, MetricKind.Dollars, Estimated: false),
                new(1500, MetricKind.Count, "tokens")
            }));
        var store = StoreWith(snapshot, OpenCodeDescriptors.ToArray());

        var badges = store.StatusBadges("opencode");
        Assert.Equal(2, badges.Count);
        Assert.Equal("Unsupported Schema", badges[0].Headline);
        Assert.Equal(WidgetData.MeterSeverity.Critical, badges[0].GetMeterState(Now).Severity);
        Assert.Equal("Partial Data", badges[1].Headline);
        Assert.Equal(WidgetData.MeterSeverity.Warning, badges[1].GetMeterState(Now).Severity);

        var today = store.Data(Descriptor("opencode.today"));
        Assert.True(today.HasData);
        Assert.Equal("$0.80", today.Headline);
    }

    [Fact]
    public void DescriptorBackedBadgesAndGenericStatusLines_AreNotEmittedAsCardStatusBadges()
    {
        var grok = new AIUsage.Core.Providers.Grok.GrokProvider();
        var grokSnapshot = new ProviderSnapshot(
            "grok",
            "Grok",
            "SuperGrok",
            new List<MetricLine>
            {
                new MetricLine.Badge("Pay as you go", "Disabled", "#a3a3a3"),
                MetricLine.NoUsageData
            },
            Now);
        var registry = new WidgetRegistry(new List<Provider> { grok.Provider }, grok.WidgetDescriptors);
        var cache = new ProviderSnapshotCache(new InMemorySettingsStore(), now: () => Now);
        cache.Store(grokSnapshot);
        var store = new WidgetDataStore(registry, new List<IProviderRuntime>(), cache, now: () => Now);

        Assert.Empty(store.StatusBadges("grok"));
        var payAsYouGo = store.Data(grok.WidgetDescriptors.Single(d => d.Id == "grok.payAsYouGo"));
        Assert.True(payAsYouGo.HasData);
        Assert.Equal("Disabled", payAsYouGo.Headline);
    }
}
