using AIUsage.Core.Services;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Services;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.2", true)]
    [InlineData("0.1.2", "0.1.2", false)]
    [InlineData("0.1.1", "0.1.2", false)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.2", "0.1.9", true)]
    [InlineData("0.2.0", "0.2", false)]
    [InlineData("0.2.0-beta.1", "0.1.2", true)]
    public void IsNewer_ComparesDottedVersions(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(candidate, current));
    }

    [Fact]
    public async Task CheckNowAsync_ReportsUpdate_WhenLatestTagIsNewer()
    {
        var http = new FakeHttpClient();
        http.Enqueue(HttpResponseFixture.Json("""{"tag_name":"v0.2.0","html_url":"https://example.com/releases/v0.2.0"}"""));
        var checker = new UpdateChecker(http, new InMemorySettingsStore());

        var outcome = await checker.CheckNowAsync("0.1.2");

        Assert.True(outcome.IsUpdateAvailable);
        Assert.Equal("0.2.0", outcome.LatestVersion);
        Assert.Equal("https://example.com/releases/v0.2.0", outcome.ReleaseUrl);
    }

    [Fact]
    public async Task CheckNowAsync_ReportsNoUpdate_WhenCurrentIsLatest()
    {
        var http = new FakeHttpClient();
        http.Enqueue(HttpResponseFixture.Json("""{"tag_name":"v0.1.2","html_url":"https://example.com/releases/v0.1.2"}"""));
        var checker = new UpdateChecker(http, new InMemorySettingsStore());

        var outcome = await checker.CheckNowAsync("0.1.2");

        Assert.False(outcome.IsUpdateAvailable);
        Assert.Equal("0.1.2", outcome.LatestVersion);
    }

    [Fact]
    public async Task CheckNowAsync_ReturnsNoUpdate_WhenRequestFails()
    {
        var http = new FakeHttpClient { Fault = new HttpClientError("boom") };
        var checker = new UpdateChecker(http, new InMemorySettingsStore());

        var outcome = await checker.CheckNowAsync("0.1.2");

        Assert.False(outcome.IsUpdateAvailable);
        Assert.Null(outcome.LatestVersion);
    }

    [Fact]
    public async Task CheckNowAsync_ReturnsNoUpdate_OnNon200Status()
    {
        var http = new FakeHttpClient();
        http.Enqueue(HttpResponseFixture.Empty(404));
        var checker = new UpdateChecker(http, new InMemorySettingsStore());

        var outcome = await checker.CheckNowAsync("0.1.2");

        Assert.False(outcome.IsUpdateAvailable);
        Assert.Null(outcome.LatestVersion);
    }

    [Fact]
    public async Task CheckIfDueAsync_SkipsNetworkCall_WithinThrottleWindow()
    {
        var http = new FakeHttpClient();
        http.Enqueue(HttpResponseFixture.Json("""{"tag_name":"v0.2.0","html_url":"https://example.com"}"""));
        var settings = new InMemorySettingsStore();
        var now = DateTimeOffset.Parse("2026-07-21T10:00:00Z");
        var checker = new UpdateChecker(http, settings, () => now);

        var first = await checker.CheckIfDueAsync("0.1.2");
        Assert.NotNull(first);
        Assert.Single(http.Requests);

        now = now.AddHours(1);
        var second = await checker.CheckIfDueAsync("0.1.2");

        Assert.Null(second);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task CheckIfDueAsync_ChecksAgain_AfterThrottleWindowElapses()
    {
        var http = new FakeHttpClient();
        http.Enqueue(HttpResponseFixture.Json("""{"tag_name":"v0.1.2","html_url":"https://example.com"}"""));
        http.Enqueue(HttpResponseFixture.Json("""{"tag_name":"v0.2.0","html_url":"https://example.com"}"""));
        var settings = new InMemorySettingsStore();
        var now = DateTimeOffset.Parse("2026-07-21T10:00:00Z");
        var checker = new UpdateChecker(http, settings, () => now);

        await checker.CheckIfDueAsync("0.1.2");

        now = now.AddHours(25);
        var outcome = await checker.CheckIfDueAsync("0.1.2");

        Assert.NotNull(outcome);
        Assert.True(outcome!.IsUpdateAvailable);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task CheckIfDueAsync_SuppressesUpdate_ForSkippedVersion()
    {
        var http = new FakeHttpClient();
        http.Enqueue(HttpResponseFixture.Json("""{"tag_name":"v0.2.0","html_url":"https://example.com"}"""));
        var settings = new InMemorySettingsStore();
        var checker = new UpdateChecker(http, settings);
        checker.SkipVersion("0.2.0");

        var outcome = await checker.CheckIfDueAsync("0.1.2");

        Assert.NotNull(outcome);
        Assert.False(outcome!.IsUpdateAvailable);
    }
}
