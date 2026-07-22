using AIUsage.Core.Providers.Kiro;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

public class KiroProviderTests
{
    private const string AuthPath = "~/.aws/sso/cache/kiro-auth-token.json";

    private static InMemoryFileSystem MakeAuthFile(string accessToken, string refreshToken) =>
        new InMemoryFileSystem().Write(AuthPath, $$"""
        {
          "accessToken": "{{accessToken}}",
          "refreshToken": "{{refreshToken}}",
          "profileArn": "arn:aws:codewhisperer:us-east-1:111111111111:profile/ABC",
          "region": "us-east-1"
        }
        """);

    [Fact]
    public async Task RefreshAsync_RejectedRefreshToken_DoesNotRetryOverNetworkOnNextCall()
    {
        // Regression test for a forced-logout bug found against a real account: once AWS rejects a
        // refresh token, every subsequent 5-minute refresh cycle kept retrying the SAME dead token
        // over the network. Repeating that indefinitely was observed to eventually invalidate the
        // live Kiro IDE session too (AWS revoking the whole token family), not just this app's copy.
        // The fix is that a refresh token AWS has already rejected must never be retried again in the
        // same process — this test drives two full RefreshAsync() calls and asserts the refresh
        // endpoint is hit at most once.
        var files = MakeAuthFile("stale-access-token", "dead-refresh-token");
        var authStore = new KiroAuthStore(files);
        var http = new FakeHttpClient();
        var usageClient = new KiroUsageClient(http);
        var provider = new KiroProvider(authStore, usageClient, () => DateTimeOffset.UtcNow);

        // First RefreshAsync(): getUsageLimits rejects with 401, triggering ProviderAuthRetry's
        // refresh-and-retry-once flow; the refresh endpoint itself also rejects (400).
        http.Enqueue(HttpResponseFixture.Empty(401)); // getUsageLimits (initial attempt)
        http.Enqueue(HttpResponseFixture.Empty(400)); // refreshDesktopToken (rejected)

        var first = await provider.RefreshAsync();
        Assert.Contains(first.Lines, l => l.IsError);
        var refreshCallsAfterFirst = http.Requests.Count(r => r.Url.Host.Contains("auth.desktop.kiro.dev"));
        Assert.Equal(1, refreshCallsAfterFirst);

        // Second RefreshAsync(): getUsageLimits rejects with 401 again (same stale token still on
        // disk), but this time the provider must recognize the refresh token as already-dead and
        // fail WITHOUT calling the refresh endpoint a second time.
        http.Enqueue(HttpResponseFixture.Empty(401)); // getUsageLimits (initial attempt)

        var second = await provider.RefreshAsync();
        Assert.Contains(second.Lines, l => l.IsError);
        var refreshCallsAfterSecond = http.Requests.Count(r => r.Url.Host.Contains("auth.desktop.kiro.dev"));
        Assert.Equal(1, refreshCallsAfterSecond); // still 1, not 2 — no repeat network call
    }

    [Fact]
    public async Task RefreshAsync_RotatedTokenOnDisk_RetriesNormally()
    {
        // If the refresh token on disk changes (the owning Kiro IDE rotated it itself), the dead-token
        // guard must not block the new token from being tried.
        var files = MakeAuthFile("stale-access-token", "dead-refresh-token");
        var authStore = new KiroAuthStore(files);
        var http = new FakeHttpClient();
        var usageClient = new KiroUsageClient(http);
        var provider = new KiroProvider(authStore, usageClient, () => DateTimeOffset.UtcNow);

        http.Enqueue(HttpResponseFixture.Empty(401));
        http.Enqueue(HttpResponseFixture.Empty(400));
        await provider.RefreshAsync();

        // Simulate the Kiro IDE rotating the refresh token on disk between cycles.
        files.Write(AuthPath, """
        {
          "accessToken": "new-access-token",
          "refreshToken": "new-refresh-token",
          "profileArn": "arn:aws:codewhisperer:us-east-1:111111111111:profile/ABC",
          "region": "us-east-1"
        }
        """);

        http.Enqueue(HttpResponseFixture.Json("""{"usageBreakdownList":[]}"""));

        var result = await provider.RefreshAsync();
        Assert.DoesNotContain(result.Lines, l => l.IsError);
    }
}
