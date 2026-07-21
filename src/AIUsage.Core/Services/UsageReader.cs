using AIUsage.Core.Providers;
using AIUsage.Core.Stores;

namespace AIUsage.Core.Services;

public sealed record UsageReadResult(string Json, List<string> Warnings);

public sealed class UnknownProviderException : Exception
{
    public UnknownProviderException(string providerId) : base($"Unknown provider: {providerId}") { }
}

/// <summary>
/// One-shot access to the same provider cache and refresh engine the tray app uses. Direct port of the
/// Swift UsageReader: shares the exact same persisted <see cref="ProviderSnapshotCache"/> file (so the
/// CLI and the tray app never disagree about "is this fresh"), and now routes the actual response
/// through <see cref="LocalUsageApi"/>'s pure `/v1/limits` responder — the exact same wire shape the
/// local HTTP API and CLI both serve (see docs/local-http-api.md). Owns no timer, tray icon, or other
/// long-lived app service; it constructs its own provider set for the duration of one read.
/// </summary>
public sealed class UsageReader
{
    private readonly List<IProviderRuntime>? _providersOverride;

    public UsageReader(List<IProviderRuntime>? providersOverride = null)
    {
        _providersOverride = providersOverride;
    }

    public async Task<UsageReadResult> ReadAsync(string? requestedProviderId = null, bool force = false, CancellationToken cancellationToken = default)
    {
        var providers = _providersOverride ?? ProviderCatalog.Make();
        var registry = WidgetRegistry.FromProviders(providers);
        var knownIds = new HashSet<string>(registry.Providers.Select(p => p.Id));

        var requestedToken = requestedProviderId?.ToLowerInvariant();
        if (requestedToken is not null && !knownIds.Contains(requestedToken))
        {
            throw new UnknownProviderException(requestedToken);
        }

        var enablement = new ProviderEnablementStore();
        bool IncludesProvider(string id) => requestedToken is not null ? id == requestedToken : enablement.IsEnabled(id);

        var cache = new ProviderSnapshotCache(allowsPersistedFreshness: true);
        var orderedIds = registry.Providers.Select(p => p.Id).ToList();
        var enabledOrderedIds = orderedIds.Where(IncludesProvider).ToList();

        var cachedSnapshots = cache.LoadSnapshots(orderedIds);
        var needsRefresh = force || enabledOrderedIds.Any(id => cache.Snapshot(id) is null);

        var snapshots = cachedSnapshots;
        var errors = new Dictionary<string, string>();
        var warnings = new List<string>();

        if (needsRefresh)
        {
            var dataStore = new WidgetDataStore(registry, providers, cache, IncludesProvider);
            if (requestedToken is not null)
            {
                await dataStore.RefreshAsync(requestedToken, force, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await dataStore.RefreshAllAsync(force, cancellationToken).ConfigureAwait(false);
            }
            snapshots = dataStore.Snapshots;
            errors = new Dictionary<string, string>(dataStore.ProviderErrors);
            warnings = orderedIds.Where(errors.ContainsKey).Select(id => $"{id}: {errors[id]}").ToList();
        }

        var state = new LocalUsageApi.State(
            EnabledOrderedIds: enabledOrderedIds,
            KnownIds: knownIds,
            Snapshots: snapshots,
            LimitDescriptors: registry.LimitDescriptorsByProvider,
            Errors: errors);

        var path = requestedToken is not null ? $"/v1/limits/{requestedToken}" : "/v1/limits";
        var response = LocalUsageApi.Respond("GET", path, state);
        var json = response.Body is { } body ? System.Text.Encoding.UTF8.GetString(body) : "{}";
        return new UsageReadResult(json, warnings);
    }
}
