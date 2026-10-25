using AIUsage.Core.Providers.OpenCode;
using AIUsage.Core.Services;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Providers;

public class OpenCodeGoStatusClientTests
{
    private const string Sample = """
        {
          "product": "go",
          "access": {
            "startsAt": "2026-10-21T12:22:03Z",
            "endsAt": "2026-11-21T12:22:03Z",
            "cancelAtPeriodEnd": false,
            "meters": {
              "fiveHour": {
                "startsAt": "2026-10-22T09:18:01Z",
                "resetsAt": "2026-10-22T14:18:01Z",
                "limitMicroCents": "1200000000",
                "usedMicroCents": "36459826"
              },
              "week": {
                "startsAt": "2026-10-19T00:00:00Z",
                "resetsAt": "2026-10-26T00:00:00Z",
                "limitMicroCents": "3000000000",
                "usedMicroCents": 537179900
              },
              "month": {
                "resetsAt": "2026-11-21T12:22:03Z",
                "limitMicroCents": "6000000000",
                "usedMicroCents": 537179900
              }
            }
          }
        }
        """;

    private static OpenCodeCredential Credential(
        string origin = "https://opencode.ai",
        string? orgId = "workspace-123") =>
        new(OpenCodeCredentialKind.OAuth, "secret-access-token", orgId, null, new Uri(origin));

    private static (OpenCodeGoStatusClient Client, FakeHttpClient Http) ClientWith(string json, int status = 200)
    {
        var http = new FakeHttpClient();
        http.Enqueue(HttpResponseFixture.Json(json, status));
        return (new OpenCodeGoStatusClient(http), http);
    }

    [Fact]
    public async Task ParsesAuthoritativeQuotaAndActualResets()
    {
        var (client, http) = ClientWith(Sample);

        var status = await client.FetchAsync(Credential());

        Assert.Equal("go", status.Product);
        Assert.True(status.HasAccess);
        Assert.Equal(0.36459826, status.FiveHour!.UsedUsd, 8);
        Assert.Equal(12.0, status.FiveHour.LimitUsd, 8);
        Assert.Equal(3.0383, status.FiveHour.PercentUsed, 4);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 14, 18, 1, TimeSpan.Zero), status.FiveHour.ResetsAt);

        Assert.Equal(5.371799, status.Week!.UsedUsd, 8);
        Assert.Equal(30.0, status.Week.LimitUsd, 8);
        Assert.Equal(17.9060, status.Week.PercentUsed, 4);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), status.Week.ResetsAt);

        Assert.Equal(5.371799, status.Month!.UsedUsd, 8);
        Assert.Equal(60.0, status.Month.LimitUsd, 8);
        Assert.Equal(8.9530, status.Month.PercentUsed, 4);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 12, 22, 3, TimeSpan.Zero), status.Month.ResetsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 12, 22, 3, TimeSpan.Zero), status.Month.StartsAt);
        Assert.Equal((long)TimeSpan.FromDays(31).TotalMilliseconds, status.Month.PeriodDurationMs);

        var request = Assert.Single(http.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://opencode.ai/console/api/go/status", request.Url.ToString());
        Assert.Equal("Bearer secret-access-token", request.Headers["Authorization"]);
        Assert.Equal("workspace-123", request.Headers["x-org-id"]);
        Assert.False(request.FollowRedirects);
    }

    [Fact]
    public async Task SupportsGoPlusDynamically()
    {
        var json = Sample
            .Replace("\"product\": \"go\"", "\"product\": \"go-plus\"")
            .Replace("1200000000", "1600000000")
            .Replace("3000000000", "4000000000")
            .Replace("6000000000", "8000000000");
        var (client, _) = ClientWith(json);

        var status = await client.FetchAsync(Credential());

        Assert.Equal("go-plus", status.Product);
        Assert.True(status.HasAccess);
        Assert.Equal(16.0, status.FiveHour!.LimitUsd, 8);
        Assert.Equal(40.0, status.Week!.LimitUsd, 8);
        Assert.Equal(80.0, status.Month!.LimitUsd, 8);
        Assert.Equal("Go Plus", OpenCodeUsageMapper.PlanName(status));
    }

    [Fact]
    public async Task NullAccess_IsLegitimateNoActiveGo()
    {
        var (client, _) = ClientWith("""{"product":"go","access":null}""");

        var status = await client.FetchAsync(Credential());

        Assert.False(status.HasAccess);
        Assert.Null(status.FiveHour);
        Assert.Null(status.Week);
        Assert.Null(status.Month);
    }

    [Fact]
    public async Task NullRoot_IsLegitimateNoActiveGo()
    {
        var (client, _) = ClientWith("null");

        var status = await client.FetchAsync(Credential());

        Assert.False(status.HasAccess);
        Assert.Null(status.FiveHour);
        Assert.Null(status.Week);
        Assert.Null(status.Month);
    }

    [Fact]
    public async Task MissingAccess_IsAChangedContract_NotNoActiveGo()
    {
        var (client, _) = ClientWith("""{"product":"go"}""");

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() => client.FetchAsync(Credential()));

        Assert.Equal(OpenCodeQuotaErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task FiveHourWithoutRollingReset_KeepsRealLimitAndUsedWithoutCountdown()
    {
        var json = """
            {
              "product": "go",
              "access": {
                "startsAt": "2026-10-21T12:22:03Z",
                "endsAt": "2026-11-21T12:22:03Z",
                "meters": {
                  "fiveHour": { "startsAt": null, "resetsAt": null, "limitMicroCents": 1200000000, "usedMicroCents": 36459826 },
                  "week": { "resetsAt": "2026-10-26T00:00:00Z", "limitMicroCents": 3000000000, "usedMicroCents": 537179900 },
                  "month": { "resetsAt": "2026-11-21T12:22:03Z", "limitMicroCents": 6000000000, "usedMicroCents": 537179900 }
                }
              }
            }
            """;
        var (client, _) = ClientWith(json);

        var status = await client.FetchAsync(Credential());

        Assert.True(status.HasAccess);
        Assert.Equal(0.36459826, status.FiveHour!.UsedUsd, 8);
        Assert.Equal(12.0, status.FiveHour.LimitUsd, 8);
        Assert.Null(status.FiveHour.ResetsAt);
        Assert.Null(status.FiveHour.StartsAt);
        Assert.Null(status.FiveHour.PeriodDurationMs);
        Assert.NotNull(status.Week);
        Assert.NotNull(status.Month);
    }

    [Theory]
    [InlineData("week")]
    [InlineData("month")]
    public async Task WeekAndMonthRequireResets(string meterName)
    {
        var json = Sample.Replace(
            "\"resetsAt\": \"2026-10-26T00:00:00Z\"",
            meterName == "week" ? "\"resetsAt\": null" : "\"resetsAt\": \"2026-10-26T00:00:00Z\"");
        if (meterName == "month")
        {
            json = json.Replace("\"resetsAt\": \"2026-11-21T12:22:03Z\"", "\"resetsAt\": null");
        }
        var (client, _) = ClientWith(json);

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() => client.FetchAsync(Credential()));

        Assert.Equal(OpenCodeQuotaErrorKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData("2026-10-22T14:18:01Z", "2026-10-22T14:18:01Z")]
    [InlineData("2026-10-22T14:18:02Z", "2026-10-22T14:18:01Z")]
    public async Task SuppliedStartsMustPrecedeResets(string startsAt, string resetsAt)
    {
        var json = Sample
            .Replace("\"startsAt\": \"2026-10-22T09:18:01Z\"", $"\"startsAt\": \"{startsAt}\"")
            .Replace("\"resetsAt\": \"2026-10-22T14:18:01Z\"", $"\"resetsAt\": \"{resetsAt}\"");
        var (client, _) = ClientWith(json);

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() => client.FetchAsync(Credential()));

        Assert.Equal(OpenCodeQuotaErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task AccessStartMustPrecedeAccessEnd()
    {
        var json = Sample.Replace("\"endsAt\": \"2026-11-21T12:22:03Z\"", "\"endsAt\": \"2026-10-21T12:22:03Z\"");
        var (client, _) = ClientWith(json);

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() => client.FetchAsync(Credential()));

        Assert.Equal(OpenCodeQuotaErrorKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData("""{"product":"go","access":{"meters":{"fiveHour":{"resetsAt":"2026-10-22T14:18:01Z","limitMicroCents":1200000000,"usedMicroCents":-1},"week":{},"month":{}}}}""")]
    [InlineData("""{"product":"go","access":{"meters":{"fiveHour":{"resetsAt":"2026-10-22T14:18:01Z","limitMicroCents":0,"usedMicroCents":0},"week":{},"month":{}}}}""")]
    [InlineData("""{"product":"go","access":{"meters":{"fiveHour":{"resetsAt":"not-a-date","limitMicroCents":1200000000,"usedMicroCents":1},"week":{},"month":{}}}}""")]
    [InlineData("""{"product":"go","access":{"meters":{"fiveHour":{"resetsAt":"2026-10-22T14:18:01Z","limitMicroCents":"nope","usedMicroCents":1},"week":{},"month":{}}}}""")]
    [InlineData("""{"product":"go","access":{"meters":{"fiveHour":{"resetsAt":"2026-10-22T14:18:01Z","limitMicroCents":1200000000,"usedMicroCents":1}}}}""")]
    public async Task MalformedOrInvalidMeters_FailWithoutFabricatingValues(string json)
    {
        var (client, _) = ClientWith(json);

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() => client.FetchAsync(Credential()));

        Assert.Equal(OpenCodeQuotaErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task InvalidJson_Fails()
    {
        var (client, _) = ClientWith("{not json");

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() => client.FetchAsync(Credential()));

        Assert.Equal(OpenCodeQuotaErrorKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AuthRejection_IsReported(int statusCode)
    {
        var (client, _) = ClientWith("{}", statusCode);

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() => client.FetchAsync(Credential()));

        Assert.Equal(OpenCodeQuotaErrorKind.AuthRejected, error.Kind);
        Assert.Equal(statusCode, error.StatusCode);
    }

    [Fact]
    public async Task Redirect_IsNotFollowed()
    {
        var (client, _) = ClientWith("{}", 302);

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() => client.FetchAsync(Credential()));

        Assert.Equal(OpenCodeQuotaErrorKind.RedirectRejected, error.Kind);
    }

    [Fact]
    public async Task Timeout_IsReported()
    {
        var http = new FakeHttpClient { Fault = new TaskCanceledException("timeout") };
        var client = new OpenCodeGoStatusClient(http);

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() => client.FetchAsync(Credential()));

        Assert.Equal(OpenCodeQuotaErrorKind.Timeout, error.Kind);
    }

    [Fact]
    public async Task CallerCancellation_IsPropagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var http = new FakeHttpClient { Fault = new OperationCanceledException(cts.Token) };
        var client = new OpenCodeGoStatusClient(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(Credential(), cts.Token));
    }

    [Fact]
    public async Task UnsafeCredentialToken_IsRejectedBeforeAnyRequest()
    {
        var http = new FakeHttpClient();
        var client = new OpenCodeGoStatusClient(http);

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() =>
            client.FetchAsync(new OpenCodeCredential(
                OpenCodeCredentialKind.OAuth, "bad\ntoken", "org", null, OpenCodeConsoleOrigin.Canonical)));

        Assert.Equal(OpenCodeQuotaErrorKind.InvalidCredential, error.Kind);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task UntrustedOrigin_IsRejectedBeforeAnyRequest()
    {
        var http = new FakeHttpClient();
        var client = new OpenCodeGoStatusClient(http);

        var error = await Assert.ThrowsAsync<OpenCodeQuotaError>(() =>
            client.FetchAsync(Credential(origin: "https://evil.example")));

        Assert.Equal(OpenCodeQuotaErrorKind.UntrustedOrigin, error.Kind);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task RetiredConsoleHost_IsAcceptedAsCanonicalAlias()
    {
        var normalized = OpenCodeConsoleOrigin.Normalize("https://console.opencode.ai");
        var (client, _) = ClientWith(Sample);

        var status = await client.FetchAsync(
            new OpenCodeCredential(OpenCodeCredentialKind.OAuth, "token", "org", null, normalized!));

        Assert.True(status.HasAccess);
    }

    [Theory]
    [InlineData("http://opencode.ai")]
    [InlineData("https://user:pass@opencode.ai")]
    [InlineData("https://evil.example")]
    [InlineData("https://opencode.ai.evil.example")]
    [InlineData("https://opencode.ai/other")]
    public void ArbitraryServerMetadata_IsRejected(string server)
    {
        Assert.Null(OpenCodeConsoleOrigin.Normalize(server));
    }
}
