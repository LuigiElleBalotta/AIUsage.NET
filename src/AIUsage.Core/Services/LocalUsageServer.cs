using System.Net;
using System.Net.Sockets;
using System.Text;
using AIUsage.Core.Support;

namespace AIUsage.Core.Services;

/// <summary>
/// Loopback-only HTTP/1.1 listener for the read-only usage API on <c>127.0.0.1:6736</c>. Starts with
/// the app; when the port is already taken the feature is silently disabled for the session (matching
/// the original app). At most 16 requests are served concurrently — beyond that a connection gets
/// <c>503 {"error":"server_busy"}</c> immediately. Direct port of the Swift LocalUsageServer, using
/// <see cref="TcpListener"/> instead of Network.framework.
/// </summary>
public sealed class LocalUsageServer : IDisposable
{
    public const int Port = 6736;
    private const int MaxConcurrentConnections = 16;
    private const int HeadLimit = 8192;

    private readonly Func<LocalUsageApi.State> _state;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _activeConnections;

    public bool IsRunning => _listener is not null;

    public LocalUsageServer(Func<LocalUsageApi.State> state)
    {
        _state = state;
    }

    /// <summary>Starts listening. No-op if already running. Disables itself silently (logs at Info)
    /// if the port is already in use or the listener otherwise fails to bind — never throws.</summary>
    public void Start()
    {
        if (_listener is not null) return;

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            _listener = listener;
        }
        catch (Exception ex)
        {
            AppLog.Info(LogTag.LocalApi, $"disabled: {ex.Message}");
            _listener = null;
            return;
        }

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        AppLog.Info(LogTag.LocalApi, $"listening on 127.0.0.1:{Port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* best effort */ }
        _listener = null;
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort shutdown */ }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = HandleConnectionAsync(client, token);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken token)
    {
        using var _client = client;
        // One NetworkStream for the whole connection lifecycle: TcpClient.GetStream() returns a
        // stream that owns the socket, so disposing it (or letting a nested `using` dispose it early)
        // closes the underlying connection — fetching it again later would then fail. Get it once
        // here and pass it to both the read and the write side.
        using var stream = client.GetStream();

        if (Interlocked.Increment(ref _activeConnections) > MaxConcurrentConnections)
        {
            Interlocked.Decrement(ref _activeConnections);
            try { await SendAsync(stream, LocalUsageApi.Busy, token).ConfigureAwait(false); } catch { /* best effort */ }
            return;
        }

        try
        {
            var head = await ReadHeadAsync(stream, token).ConfigureAwait(false);
            if (head is null) return;

            var (method, path) = ParseRequestLine(head);
            AppLog.Debug(LogTag.LocalApi, $"{method} {path}");
            var response = LocalUsageApi.Respond(method, path, _state());
            await SendAsync(stream, response, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Debug(LogTag.LocalApi, $"connection error: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    /// <summary>Reads until the end of the request head (\r\n\r\n). GET/OPTIONS bodies are irrelevant,
    /// so the head is all the router needs.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[HeadLimit];
        var total = 0;

        while (total < HeadLimit)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(total, HeadLimit - total), token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            if (read <= 0) break;
            total += read;

            var text = Encoding.UTF8.GetString(buffer, 0, total);
            var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headEnd >= 0) return text[..headEnd];
        }
        return null;
    }

    /// <summary>Parse the HTTP request line into (method, path). Tolerates an empty/malformed head —
    /// yields ("", "/") rather than throwing, so a stray loopback payload routes to a normal 404.</summary>
    internal static (string Method, string Path) ParseRequestLine(string head)
    {
        var requestLine = head.Split("\r\n", 2)[0];
        var parts = requestLine.Split(' ');
        var method = parts.Length > 0 ? parts[0] : "";
        var path = parts.Length > 1 ? parts[1] : "/";
        return (method, path);
    }

    private static async Task SendAsync(NetworkStream stream, LocalUsageApi.Response response, CancellationToken token)
    {
        var reason = response.Status switch
        {
            200 => "OK",
            204 => "No Content",
            404 => "Not Found",
            405 => "Method Not Allowed",
            503 => "Service Unavailable",
            _ => "OK"
        };

        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {response.Status} {reason}\r\n");
        head.Append("Access-Control-Allow-Origin: *\r\n");
        head.Append("Access-Control-Allow-Methods: GET, OPTIONS\r\n");
        head.Append("Access-Control-Allow-Headers: Content-Type\r\n");
        head.Append("Connection: close\r\n");

        if (response.Body is { } body)
        {
            head.Append("Content-Type: application/json\r\n");
            head.Append($"Content-Length: {body.Length}\r\n\r\n");
            var headBytes = Encoding.UTF8.GetBytes(head.ToString());
            await stream.WriteAsync(headBytes, token).ConfigureAwait(false);
            await stream.WriteAsync(body, token).ConfigureAwait(false);
        }
        else
        {
            head.Append("Content-Length: 0\r\n\r\n");
            var headBytes = Encoding.UTF8.GetBytes(head.ToString());
            await stream.WriteAsync(headBytes, token).ConfigureAwait(false);
        }
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
