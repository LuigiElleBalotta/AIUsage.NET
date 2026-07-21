using AIUsage.Core.Services;

namespace AIUsage.Core.Tests.TestHelpers;

/// <summary>Minimal scripted IHttpClient for tests that need to exercise a real HTTP-calling code
/// path (e.g. UpdateChecker) without touching the network. Returns whatever is queued via
/// <see cref="Enqueue"/>, or throws if the queue is exhausted and no <see cref="Fault"/> is set.</summary>
public sealed class FakeHttpClient : IHttpClient
{
    private readonly Queue<HttpResponseResult> _responses = new();
    public List<HttpRequestSpec> Requests { get; } = new();
    public Exception? Fault { get; set; }

    public void Enqueue(HttpResponseResult response) => _responses.Enqueue(response);

    public Task<HttpResponseResult> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Fault is not null) throw Fault;
        if (_responses.Count == 0) throw new InvalidOperationException("FakeHttpClient: no queued response.");
        return Task.FromResult(_responses.Dequeue());
    }
}
