using AIUsage.Core.Services;

namespace AIUsage.Core.Stores;

/// <summary>
/// The single source of truth for which providers are turned on. Direct port of the Swift
/// ProviderEnablementStore's enabled-list mode (the legacy disabled-list mode and its migration are
/// omitted — this is a fresh port with no upgrade path to support). Backed by ISettingsStore (the
/// Windows equivalent of UserDefaults.standard) instead of NotificationCenter for the change signal —
/// plain C# events serve the same "wake the refresh loop early" purpose.
/// </summary>
public sealed class ProviderEnablementStore
{
    private const string EnabledStorageKey = "aiusage.enabledProviders.v1";
    private const string KnownStorageKey = "aiusage.knownProviders.v1";

    /// <summary>Raised after any real enablement-set change, so the refresh loop can wake early.</summary>
    public event Action? Changed;
    /// <summary>Raised the moment a provider turns ON (not on disable, not on a no-op re-set).</summary>
    public event Action<string>? ProviderEnabled;

    private readonly ISettingsStore _settings;
    private HashSet<string>? _enabledIds;
    private HashSet<string> _knownIds;

    public ProviderEnablementStore(ISettingsStore? settings = null)
    {
        _settings = settings ?? FileSettingsStore.Shared;
        var enabled = ReadList(EnabledStorageKey);
        _enabledIds = enabled is not null ? new HashSet<string>(enabled) : null;
        _knownIds = new HashSet<string>(ReadList(KnownStorageKey) ?? new List<string>());
    }

    /// <summary>Null means "never customized" (all-on legacy default / not yet seeded). Exposed so
    /// FirstRunSeeder can tell a fresh store apart from one the user has already touched.</summary>
    public HashSet<string>? EnabledIds => _enabledIds is null ? null : new HashSet<string>(_enabledIds);

    /// <summary>Every provider id this install has ever registered via <see cref="RegisterKnownProviders"/>.
    /// Empty means the install predates that tracking — NewProviderSeeder uses this to distinguish
    /// "genuinely new provider" from "this store never tracked known ids at all".</summary>
    public HashSet<string> KnownIds => new(_knownIds);

    public bool IsEnabled(string id) => _enabledIds is null || _enabledIds.Contains(id);

    public void SetEnabled(bool enabled, string id)
    {
        var ids = _enabledIds is not null ? new HashSet<string>(_enabledIds) : new HashSet<string>();
        var before = _enabledIds is null ? null : new HashSet<string>(_enabledIds);
        if (enabled) ids.Add(id); else ids.Remove(id);
        if (before is not null && ids.SetEquals(before)) return;

        _enabledIds = ids;
        WriteList(EnabledStorageKey, ids);
        if (enabled) ProviderEnabled?.Invoke(id);
        Changed?.Invoke();
    }

    public HashSet<string> RegisterKnownProviders(IEnumerable<string> ids)
    {
        var newIds = new HashSet<string>(ids);
        newIds.ExceptWith(_knownIds);
        if (newIds.Count == 0) return newIds;
        _knownIds.UnionWith(newIds);
        WriteList(KnownStorageKey, _knownIds);
        return newIds;
    }

    public void SeedEnabledProviders(IEnumerable<string> ids)
    {
        var idSet = new HashSet<string>(ids);
        var newlyEnabled = idSet.Where(id => !IsEnabled(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var changed = _enabledIds is null || !_enabledIds.SetEquals(idSet);
        _enabledIds = idSet;
        WriteList(EnabledStorageKey, idSet);
        if (!changed) return;
        foreach (var id in newlyEnabled) ProviderEnabled?.Invoke(id);
        Changed?.Invoke();
    }

    private List<string>? ReadList(string key)
    {
        var raw = _settings.GetString(key);
        if (raw is null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw);
        }
        catch
        {
            return null;
        }
    }

    private void WriteList(string key, IEnumerable<string> ids)
    {
        _settings.SetString(key, System.Text.Json.JsonSerializer.Serialize(ids.ToList()));
    }
}
