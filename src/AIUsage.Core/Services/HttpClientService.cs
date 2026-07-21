using AIUsage.Core.Support;

namespace AIUsage.Core.Services;

public sealed class HttpRequestSpec
{
    public required string Method { get; init; }
    public required Uri Url { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public byte[]? Body { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed class HttpResponseResult
{
    public required int StatusCode { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

public interface IHttpClient
{
    Task<HttpResponseResult> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider HTTP client backed by System.Net.Http.HttpClient — the Windows tray app's shared HTTP
/// client. Proxy configuration is applied via HttpClientHandler when set in
/// ~/.aiusage/config.json (see ProxyConfig).
/// </summary>
public sealed class SystemHttpClient : IHttpClient
{
    private static readonly Lazy<System.Net.Http.HttpClient> SharedClient = new(() =>
    {
        var handler = new System.Net.Http.HttpClientHandler();
        var proxy = ProxyConfig.Current;
        if (proxy is not null)
        {
            handler.Proxy = proxy.ToWebProxy();
            handler.UseProxy = true;
            AppLog.Info(LogTag.Config, $"proxy enabled {proxy.Scheme}://{proxy.Host}:{proxy.Port}");
        }
        return new System.Net.Http.HttpClient(handler);
    });

    public async Task<HttpResponseResult> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(request.Body);
        }
        foreach (var (key, value) in request.Headers)
        {
            if (message.Content is not null && IsContentHeader(key))
            {
                message.Content.Headers.TryAddWithoutValidation(key, value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await SharedClient.Value.SendAsync(message, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug(LogTag.Http, $"{request.Method} {LogRedaction.RedactUrl(request.Url.ToString())} -> transport error: {ex.Message}");
            throw;
        }

        var body = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        var headers = new Dictionary<string, string>();
        foreach (var h in response.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
        foreach (var h in response.Content.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);

        var line = $"{request.Method} {LogRedaction.RedactUrl(request.Url.ToString())} -> {(int)response.StatusCode}";
        if ((int)response.StatusCode >= 400)
        {
            AppLog.Debug(LogTag.Http, $"{line} body: {LogRedaction.BodyPreview(System.Text.Encoding.UTF8.GetString(body))}");
        }
        else
        {
            AppLog.Debug(LogTag.Http, line);
        }

        return new HttpResponseResult
        {
            StatusCode = (int)response.StatusCode,
            Headers = headers,
            Body = body
        };
    }

    private static bool IsContentHeader(string name) => name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
}

public sealed class HttpClientError : Exception
{
    public HttpClientError(string message) : base(message) { }
}
