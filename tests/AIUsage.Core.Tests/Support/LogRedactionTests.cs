using AIUsage.Core.Support;

namespace AIUsage.Core.Tests.Support;

public class LogRedactionTests
{
    [Fact]
    public void RedactValue_ShortValue_BecomesFullyRedacted()
    {
        Assert.Equal("[REDACTED]", LogRedaction.RedactValue("shortval"));
    }

    [Fact]
    public void RedactValue_LongValue_KeepsFirstAndLastFourChars()
    {
        var result = LogRedaction.RedactValue("sk-abcdefghijklmnopqrstuvwxyz");
        Assert.Equal("sk-a...wxyz", result);
    }

    [Fact]
    public void RedactLogMessage_RedactsJwt()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dQw4w9WgXcQ_m3z6xF3lJ0";
        var message = $"token={jwt} refreshed";
        var result = LogRedaction.RedactLogMessage(message);
        Assert.DoesNotContain(jwt, result);
        Assert.Contains("...", result);
    }

    [Fact]
    public void RedactLogMessage_RedactsApiKeyPrefixedValues()
    {
        var message = "using key sk-abcdefghijklmnopqrstuvwx for request";
        var result = LogRedaction.RedactLogMessage(message);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwx", result);
    }

    [Fact]
    public void RedactLogMessage_RedactsDevinSessionToken()
    {
        var message = "auth devin-session-token$abcdef1234567890 loaded";
        var result = LogRedaction.RedactLogMessage(message);
        Assert.DoesNotContain("abcdef1234567890", result);
    }

    [Fact]
    public void RedactLogMessage_RedactsAccountValue()
    {
        var message = "cache hit account=user@example.com for provider";
        var result = LogRedaction.RedactLogMessage(message);
        Assert.DoesNotContain("user@example.com", result);
        Assert.Contains("account=", result);
    }

    [Theory]
    [InlineData(@"C:\Users\alice\.claude\.credentials.json")]
    [InlineData(@"\\server\share\secret.json")]
    [InlineData("/Users/alice/.claude/.credentials.json")]
    public void RedactLogMessage_RedactsFilesystemPaths(string path)
    {
        var result = LogRedaction.RedactLogMessage($"reading {path} now");
        Assert.DoesNotContain(path, result);
        Assert.Contains("[PATH]", result);
    }

    [Fact]
    public void RedactLogMessage_LeavesOrdinaryTextUntouched()
    {
        const string message = "refresh start (2 sources: file refresh=yes expired=no)";
        Assert.Equal(message, LogRedaction.RedactLogMessage(message));
    }

    [Fact]
    public void RedactUrl_RedactsSensitiveQueryParams()
    {
        var url = "https://api.example.com/v1/data?api_key=sk-abcdefghijklmno&foo=bar";
        var result = LogRedaction.RedactUrl(url);
        Assert.DoesNotContain("sk-abcdefghijklmno", result);
        Assert.Contains("foo=bar", result);
    }

    [Fact]
    public void RedactUrl_NoQueryString_ReturnsUnchanged()
    {
        var url = "https://api.example.com/v1/data";
        Assert.Equal(url, LogRedaction.RedactUrl(url));
    }

    [Fact]
    public void RedactBody_RedactsJsonSensitiveKeys()
    {
        var body = "{\"access_token\": \"abcdefghijklmnopqrstuvwxyz\", \"other\": \"value\"}";
        var result = LogRedaction.RedactBody(body);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", result);
        Assert.Contains("\"other\": \"value\"", result);
    }

    [Fact]
    public void BodyPreview_TruncatesLongBodies()
    {
        var body = new string('a', 1000);
        var result = LogRedaction.BodyPreview(body, limit: 50);
        Assert.Contains("bytes total", result);
        Assert.True(result.Length < body.Length);
    }

    [Fact]
    public void BodyPreview_ShortBody_ReturnsFullRedactedBody()
    {
        var body = "{\"ok\": true}";
        var result = LogRedaction.BodyPreview(body, limit: 500);
        Assert.Equal(body, result);
    }
}
