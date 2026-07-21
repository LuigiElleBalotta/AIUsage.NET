using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Stores;

/// <summary>
/// Persisted provider snapshot cache with a session-freshness gate. Direct port of the Swift
/// ProviderSnapshotCache, minus the account-identity stamping (producedByIdentityKey) — that's part of
/// the multi-account Claude feature not yet ported (see PORTING_NOTES.md). Backed by ISettingsStore
/// (a JSON file) instead of UserDefaults.
/// </summary>
public sealed class ProviderSnapshotCache
{
    private const string StorageKey = "aiusage.providerSnapshots.v1";

    private readonly ISettingsStore _settings;
    private readonly TimeSpan _ttl;
    private readonly bool _allowsPersistedFreshness;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private Dictionary<string, ProviderSnapshot>? _memo;
    private readonly HashSet<string> _sessionWrites = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ProviderSnapshotCache(
        ISettingsStore? settings = null,
        TimeSpan? ttl = null,
        bool allowsPersistedFreshness = false,
        Func<DateTimeOffset>? now = null)
    {
        _settings = settings ?? FileSettingsStore.Shared;
        _ttl = ttl ?? RefreshSetting.Interval;
        _allowsPersistedFreshness = allowsPersistedFreshness;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public Dictionary<string, ProviderSnapshot> LoadSnapshots(IEnumerable<string> providerIds)
    {
        var idSet = new HashSet<string>(providerIds);
        var all = LoadPayload();
        return all.Where(kv => idSet.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public ProviderSnapshot? Snapshot(string providerId)
    {
        var all = LoadPayload();
        if (!all.TryGetValue(providerId, out var snapshot)) return null;

        bool writtenThisSession;
        lock (_lock) { writtenThisSession = _sessionWrites.Contains(providerId); }
        var age = _now() - snapshot.RefreshedAt;
        var trusted = _allowsPersistedFreshness || writtenThisSession;
        var fresh = trusted && age < _ttl;
        return fresh ? snapshot : null;
    }

    public void Store(ProviderSnapshot snapshot)
    {
        if (snapshot.Lines.Any(l => l.IsError))
        {
            AppLog.Debug(LogTag.Cache, $"skip write {snapshot.ProviderId} (error snapshot)");
            return;
        }
        AppLog.Debug(LogTag.Cache, $"write {snapshot.ProviderId}");
        lock (_lock) { _sessionWrites.Add(snapshot.ProviderId); }

        var payload = LoadPayload();
        payload[snapshot.ProviderId] = snapshot;
        Save(payload);
    }

    private Dictionary<string, ProviderSnapshot> LoadPayload()
    {
        lock (_lock)
        {
            if (_memo is not null) return _memo;
            _memo = DecodeStoredPayload();
            return _memo;
        }
    }

    private Dictionary<string, ProviderSnapshot> DecodeStoredPayload()
    {
        var raw = _settings.GetString(StorageKey);
        if (raw is null) return new Dictionary<string, ProviderSnapshot>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ProviderSnapshot>>(raw, JsonOptions)
                   ?? new Dictionary<string, ProviderSnapshot>();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Cache, $"cache decode failed, dropping stored snapshots: {ex.Message}");
            return new Dictionary<string, ProviderSnapshot>();
        }
    }

    private void Save(Dictionary<string, ProviderSnapshot> payload)
    {
        lock (_lock) { _memo = payload; }
        try
        {
            _settings.SetString(StorageKey, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Cache, $"encode failed, snapshot not persisted: {ex.Message}");
        }
    }
}
