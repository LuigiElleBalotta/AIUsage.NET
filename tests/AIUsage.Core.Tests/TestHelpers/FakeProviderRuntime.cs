using AIUsage.Core.Models;
using AIUsage.Core.Providers;

namespace AIUsage.Core.Tests.TestHelpers;

/// <summary>Minimal IProviderRuntime fake for App-layer tests (seeders, reconciliation) that only
/// need Provider identity and a controllable local-credentials probe — never a real refresh.</summary>
public sealed class FakeProviderRuntime : IProviderRuntime
{
    public Provider Provider { get; }
    public List<WidgetDescriptor> WidgetDescriptors { get; } = new();
    public bool HasLocalCredentials { get; set; }
    public int HasLocalCredentialsCallCount { get; private set; }

    public FakeProviderRuntime(string id, bool hasLocalCredentials = false)
    {
        Provider = new Provider(id, id, id);
        HasLocalCredentials = hasLocalCredentials;
    }

    public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("FakeProviderRuntime does not support RefreshAsync");

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default)
    {
        HasLocalCredentialsCallCount++;
        return Task.FromResult(HasLocalCredentials);
    }
}
