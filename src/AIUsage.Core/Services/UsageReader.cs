using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Stores;

namespace AIUsage.Core.Services;

public sealed record UsageReadResult(string Json, List<string> Warnings);

public sealed class UnknownProviderException : Exception
{
    public UnknownProviderException(string providerId) : base($"Unknown provider: {providerId}") { }
}

/// <summary>
/// One-shot access to the same provider cache and refresh engine the tray app uses. Simplified port of
/// the Swift UsageReader: shares the exact same persisted <see cref="ProviderSnapshotCache"/> file (so
/// the CLI and the tray app never disagree about "is this fresh"), but returns a plain JSON snapshot
/// dump instead of routing through the (not yet ported) LocalUsageAPI response shape — see
/// PORTING_NOTES.md. Owns no timer, tray icon, or other long-lived app service; it constructs its own
/// provider set for the duration of one read.
/// </summary>
public sealed class UsageReader
{
    private readonly List<IProviderRuntime>? _providersOverride;

    public UsageReader(List<IProviderRuntime>? providersOverride = null)
    {
        _providersOverride = providersOverride;
    }

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

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

        var payload = new Dictionary<string, object?>();
        foreach (var id in enabledOrderedIds)
        {
            if (!snapshots.TryGetValue(id, out var snapshot)) continue;
            payload[id] = new
            {
                displayName = snapshot.DisplayName,
                plan = snapshot.Plan,
                lines = snapshot.Lines,
                refreshedAt = snapshot.RefreshedAt,
                warning = snapshot.Warning,
                error = errors.GetValueOrDefault(id)
            };
        }

        var json = JsonSerializer.Serialize(requestedToken is not null && payload.Count == 1 ? payload.Values.First() : payload, OutputJsonOptions);
        return new UsageReadResult(json, warnings);
    }
}
