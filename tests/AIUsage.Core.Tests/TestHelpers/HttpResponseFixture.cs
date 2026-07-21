using System.Text;
using AIUsage.Core.Services;

namespace AIUsage.Core.Tests.TestHelpers;

/// <summary>Builds HttpResponseResult fixtures for mapper tests without needing a real HTTP call.</summary>
public static class HttpResponseFixture
{
    public static HttpResponseResult Json(string json, int statusCode = 200, Dictionary<string, string>? headers = null)
    {
        var normalizedHeaders = new Dictionary<string, string>();
        if (headers is not null)
        {
            foreach (var (k, v) in headers) normalizedHeaders[k.ToLowerInvariant()] = v;
        }
        return new HttpResponseResult
        {
            StatusCode = statusCode,
            Headers = normalizedHeaders,
            Body = Encoding.UTF8.GetBytes(json)
        };
    }

    public static HttpResponseResult Empty(int statusCode) => new()
    {
        StatusCode = statusCode,
        Headers = new Dictionary<string, string>(),
        Body = Array.Empty<byte>()
    };
}
