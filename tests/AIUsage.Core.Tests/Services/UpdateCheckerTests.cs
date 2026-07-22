using AIUsage.Core.Services;
using AIUsage.Core.Tests.TestHelpers;
using Velopack;
using Xunit;

namespace AIUsage.Core.Tests.Services;

/// <summary>Fake <see cref="IAppUpdateManager"/> for tests, avoiding any real Velopack install
/// detection or network calls.</summary>
public sealed class FakeAppUpdateManager : IAppUpdateManager
{
    public bool IsInstalled { get; set; } = true;
    public UpdateInfo? NextCheckResult { get; set; }
    public Exception? CheckFault { get; set; }
    public int CheckCallCount { get; private set; }
    public int DownloadCallCount { get; private set; }
    public VelopackAsset? AppliedAsset { get; private set; }

    public Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        CheckCallCount++;
        if (CheckFault is not null) throw CheckFault;
        return Task.FromResult(NextCheckResult);
    }

    public Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress, CancellationToken cancellationToken)
    {
        DownloadCallCount++;
        progress?.Invoke(100);
        return Task.CompletedTask;
    }

    public void ApplyUpdatesAndRestart(VelopackAsset? toApply) => AppliedAsset = toApply;

    public static UpdateInfo MakeInfo(string version) =>
        new(new VelopackAsset { Version = SemanticVersion.Parse(version) }, isDowngrade: false);
}

public class UpdateCheckerTests
{
    [Fact]
    public async Task CheckNowAsync_ReportsUpdate_WhenVelopackFindsOne()
    {
        var manager = new FakeAppUpdateManager { NextCheckResult = FakeAppUpdateManager.MakeInfo("0.2.0") };
        var checker = new UpdateChecker(manager, new InMemorySettingsStore());

        var outcome = await checker.CheckNowAsync("0.1.2");

        Assert.True(outcome.IsUpdateAvailable);
        Assert.Equal("0.2.0", outcome.LatestVersion);
        Assert.NotNull(outcome.UpdateInfo);
    }

    [Fact]
    public async Task CheckNowAsync_ReportsNoUpdate_WhenVelopackFindsNone()
    {
        var manager = new FakeAppUpdateManager { NextCheckResult = null };
        var checker = new UpdateChecker(manager, new InMemorySettingsStore());

        var outcome = await checker.CheckNowAsync("0.1.2");

        Assert.False(outcome.IsUpdateAvailable);
        Assert.Equal("0.1.2", outcome.LatestVersion);
    }

    [Fact]
    public async Task CheckNowAsync_ReturnsNoUpdate_WhenNotInstalled()
    {
        var manager = new FakeAppUpdateManager { IsInstalled = false, NextCheckResult = FakeAppUpdateManager.MakeInfo("0.2.0") };
        var checker = new UpdateChecker(manager, new InMemorySettingsStore());

        var outcome = await checker.CheckNowAsync("0.1.2");

        Assert.False(outcome.IsUpdateAvailable);
        Assert.Null(outcome.LatestVersion);
        Assert.Equal(0, manager.CheckCallCount);
    }

    [Fact]
    public async Task CheckNowAsync_ReturnsNoUpdate_WhenCheckThrows()
    {
        var manager = new FakeAppUpdateManager { CheckFault = new InvalidOperationException("network down") };
        var checker = new UpdateChecker(manager, new InMemorySettingsStore());

        var outcome = await checker.CheckNowAsync("0.1.2");

        Assert.False(outcome.IsUpdateAvailable);
        Assert.Null(outcome.LatestVersion);
    }

    [Fact]
    public async Task CheckIfDueAsync_SkipsCheck_WithinThrottleWindow()
    {
        var manager = new FakeAppUpdateManager { NextCheckResult = FakeAppUpdateManager.MakeInfo("0.2.0") };
        var settings = new InMemorySettingsStore();
        var now = DateTimeOffset.Parse("2026-07-21T10:00:00Z");
        var checker = new UpdateChecker(manager, settings, () => now);

        var first = await checker.CheckIfDueAsync("0.1.2");
        Assert.NotNull(first);
        Assert.Equal(1, manager.CheckCallCount);

        now = now.AddHours(1);
        var second = await checker.CheckIfDueAsync("0.1.2");

        Assert.Null(second);
        Assert.Equal(1, manager.CheckCallCount);
    }

    [Fact]
    public async Task CheckIfDueAsync_ChecksAgain_AfterThrottleWindowElapses()
    {
        var manager = new FakeAppUpdateManager { NextCheckResult = null };
        var settings = new InMemorySettingsStore();
        var now = DateTimeOffset.Parse("2026-07-21T10:00:00Z");
        var checker = new UpdateChecker(manager, settings, () => now);

        await checker.CheckIfDueAsync("0.1.2");

        manager.NextCheckResult = FakeAppUpdateManager.MakeInfo("0.2.0");
        now = now.AddHours(25);
        var outcome = await checker.CheckIfDueAsync("0.1.2");

        Assert.NotNull(outcome);
        Assert.True(outcome!.IsUpdateAvailable);
        Assert.Equal(2, manager.CheckCallCount);
    }

    [Fact]
    public async Task CheckIfDueAsync_SuppressesUpdate_ForSkippedVersion()
    {
        var manager = new FakeAppUpdateManager { NextCheckResult = FakeAppUpdateManager.MakeInfo("0.2.0") };
        var settings = new InMemorySettingsStore();
        var checker = new UpdateChecker(manager, settings);
        checker.SkipVersion("0.2.0");

        var outcome = await checker.CheckIfDueAsync("0.1.2");

        Assert.NotNull(outcome);
        Assert.False(outcome!.IsUpdateAvailable);
    }

    [Fact]
    public async Task DownloadAndApplyAsync_DownloadsThenApplies()
    {
        var info = FakeAppUpdateManager.MakeInfo("0.2.0");
        var manager = new FakeAppUpdateManager { NextCheckResult = info };
        var checker = new UpdateChecker(manager, new InMemorySettingsStore());
        var outcome = await checker.CheckNowAsync("0.1.2");

        await checker.DownloadAndApplyAsync(outcome);

        Assert.Equal(1, manager.DownloadCallCount);
        Assert.NotNull(manager.AppliedAsset);
    }

    [Fact]
    public async Task DownloadAndApplyAsync_Throws_WhenOutcomeHasNoUpdateInfo()
    {
        var manager = new FakeAppUpdateManager();
        var checker = new UpdateChecker(manager, new InMemorySettingsStore());
        var outcome = new UpdateChecker.Outcome(false, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => checker.DownloadAndApplyAsync(outcome));
    }
}
