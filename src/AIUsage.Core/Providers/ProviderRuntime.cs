using AIUsage.Core.Models;

namespace AIUsage.Core.Providers;

/// <summary>
/// One AI provider AIUsage can track.
/// </summary>
public interface IProviderRuntime
{
    Provider Provider { get; }
    List<WidgetDescriptor> WidgetDescriptors { get; }

    Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Cheap, local-only credential probe (files, credential store; never the network).</summary>
    Task<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken = default);
}

/// <summary>A provider that needs a user-supplied API key (OpenRouter, Z.ai).</summary>
public interface IApiKeyManaging : IProviderRuntime
{
    Models.APIKeyStatus ApiKeyStatus { get; }
    string? CurrentApiKey();
    void SaveApiKey(string key);
    void DeleteApiKey();
}
