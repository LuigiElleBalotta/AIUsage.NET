using AIUsage.Core.Models;
using AIUsage.Core.Providers;

namespace AIUsage.Core.Tests.TestHelpers;

/// <summary>An <see cref="IProviderRuntime"/> whose refresh results are scripted by the test.</summary>
public sealed class ScriptedProviderRuntime : IProviderRuntime
{
    private readonly Queue<ProviderSnapshot> _snapshots;

    public ScriptedProviderRuntime(string providerId, params ProviderSnapshot[] snapshots)
        : this(new Provider(providerId, providerId, providerId), snapshots)
    {
    }

    public ScriptedProviderRuntime(Provider provider, params ProviderSnapshot[] snapshots)
    {
        if (snapshots.Length == 0) throw new ArgumentException("At least one snapshot is required.", nameof(snapshots));
        Provider = provider;
        _snapshots = new Queue<ProviderSnapshot>(snapshots);
    }

    public Provider Provider { get; }
    public List<WidgetDescriptor> WidgetDescriptors { get; set; } = new();
    public int RefreshCount { get; private set; }

    public Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (_snapshots.Count > 1) return Task.FromResult(_snapshots.Dequeue());
        return Task.FromResult(_snapshots.Peek());
    }

    public Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
