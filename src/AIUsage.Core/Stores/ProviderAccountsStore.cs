using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Stores;

/// <summary>
/// Card-id helpers for the account-first model. Direct port of the Swift ProviderAccountID. The
/// account occupying a family's default home when first observed keeps the bare family id (e.g.
/// "claude") as its permanent record id — existing installs migrate by doing nothing. Any later
/// account of the same family mints "family@hash8" from its identity key. Windows port note: only the
/// "claude" family is wired up (see PORTING_NOTES.md — Codex multi-account is Swift-original scope,
/// not requested for this port).
/// </summary>
public static class ProviderAccountID
{
    public static readonly HashSet<string> Families = new() { "claude" };

    /// <summary>"claude@ab12cd34" — a stable, non-reversible id derived from the account's identity key.</summary>
    public static string Make(string family, string identityKey) => $"{family}@{Hash8(identityKey)}";

    /// <summary>The 8-hex-char identity digest card ids are built from.</summary>
    public static string Hash8(string identityKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identityKey.ToLowerInvariant()));
        return Convert.ToHexString(digest[..4]).ToLowerInvariant();
    }

    /// <summary>The family a card id belongs to: "claude@ab12cd34" -> "claude"; bare ids map to themselves.</summary>
    public static string Family(string cardId)
    {
        var at = cardId.IndexOf('@');
        return at >= 0 ? cardId[..at] : cardId;
    }

    /// <summary>Whether a card id names an extra account card ("claude@ab12cd34") rather than a bare provider id.</summary>
    public static bool IsAccountCard(string cardId) => cardId.Contains('@');
}

/// <summary>One place an account is signed in. "Default" is a badge on a source, never a key: it marks
/// who currently occupies the default home, and never drives ids or sort order.</summary>
public sealed class ProviderAccountSource
{
    public enum SourceKind
    {
        /// <summary>The provider's standard home for this machine (~/.claude, env override).</summary>
        DefaultHome,
        /// <summary>A custom Claude config dir (a CLAUDE_CONFIG_DIR home kept besides the default).</summary>
        ConfigDir
    }

    public SourceKind Kind { get; set; }
    public string? Anchor { get; set; }
    public bool HoldsDefaultSource { get; set; }
    /// <summary>ConfigDir only: the literal string whose hash names the source's Credential Manager item.</summary>
    public string? KeychainLiteral { get; set; }
}

/// <summary>An account as the account-first model sees it: opaque identity key, stable record id
/// minted at creation, and the sources currently attaching to it. Direct port of the Swift
/// ProviderAccountRecord.</summary>
public sealed class ProviderAccountRecord
{
    /// <summary>Stable id minted when the account is first seen; never re-derived.</summary>
    public required string Id { get; set; }
    public required string Family { get; set; }
    public required string IdentityKey { get; set; }
    public string? Label { get; set; }
    /// <summary>A user-chosen card name (Rename). Wins over Label and the id-derived fallback; never
    /// touched by reconciliation.</summary>
    public string? CustomLabel { get; set; }
    public List<ProviderAccountSource> Sources { get; set; } = new();
    /// <summary>Set by a future "Remove Account...". A tombstoned account is never resurrected by rescans.</summary>
    public bool RemovedTombstone { get; set; }

    /// <summary>The name a card carries without a rename.</summary>
    public string DerivedDisplayName
    {
        get
        {
            if (!ProviderAccountID.IsAccountCard(Id)) return Capitalize(Family);
            var label = Label?.NilIfEmpty();
            if (label is null) return Id;
            if (label.EndsWith(')') && label.LastIndexOf('(') is var open && open >= 0)
            {
                var org = label[(open + 1)..^1].Trim();
                if (org.Length > 0) return $"{Capitalize(Family)} — {org}";
            }
            return $"{Capitalize(Family)} — {label}";
        }
    }

    /// <summary>THE name resolver — the single place a rename becomes a card title.</summary>
    public string ResolvedDisplayName => CustomLabel?.NilIfEmpty() ?? DerivedDisplayName;

    private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}

/// <summary>
/// The account-first registry ("aiusage.providerAccounts.v1"). Reconciled at every launch from the
/// default-home identity reads and the config-dir scan. Direct port of the Swift ProviderAccountsStore,
/// backed by <see cref="ISettingsStore"/> instead of UserDefaults/@Observable — renames are read at
/// launch (there is no live-observing WPF surface yet, see PORTING_NOTES.md).
/// </summary>
public sealed class ProviderAccountsStore
{
    public const string StorageKey = "aiusage.providerAccounts.v1";

    private readonly ISettingsStore _settings;
    private List<ProviderAccountRecord> _records;

    public ProviderAccountsStore(ISettingsStore? settings = null)
    {
        _settings = settings ?? FileSettingsStore.Shared;
        var raw = _settings.GetString(StorageKey);
        if (raw is not null)
        {
            try
            {
                _records = JsonSerializer.Deserialize<List<ProviderAccountRecord>>(raw) ?? new();
            }
            catch (Exception ex)
            {
                AppLog.Error(LogTag.Config, $"provider-account records were undecodable; starting a fresh registry: {ex.Message}");
                _records = new();
            }
        }
        else
        {
            _records = new();
        }
    }

    public IReadOnlyList<ProviderAccountRecord> Records => _records;

    /// <summary>One account observed this launch, before reconciliation assigns (or re-finds) its record id.</summary>
    public sealed class AccountObservation
    {
        public required string Family { get; init; }
        public required string IdentityKey { get; init; }
        public string? Label { get; init; }
        public required List<ProviderAccountSource> Sources { get; init; }
    }

    /// <summary>Merges this launch's observations into the persisted set. The first account of a
    /// family gets the bare family id, a later one mints "family@hash8". Records never move or
    /// vanish here — an account that went unobserved is left as-is, except that a newly observed
    /// default-home holder takes the default badge off every sibling.</summary>
    public List<ProviderAccountRecord> Reconcile(List<AccountObservation> observations)
    {
        var updated = new List<ProviderAccountRecord>(_records);
        var changed = false;

        foreach (var observation in observations)
        {
            var index = updated.FindIndex(r => r.Family == observation.Family && r.IdentityKey == observation.IdentityKey);
            if (index >= 0)
            {
                if (updated[index].RemovedTombstone) continue;
                var record = updated[index];
                var newLabel = observation.Label ?? record.Label;
                if (record.Label != newLabel || !SourcesEqual(record.Sources, observation.Sources))
                {
                    record.Label = newLabel;
                    record.Sources = observation.Sources;
                    changed = true;
                }
            }
            else
            {
                updated.Add(new ProviderAccountRecord
                {
                    Id = AvailableId(observation, updated),
                    Family = observation.Family,
                    IdentityKey = observation.IdentityKey,
                    Label = observation.Label,
                    Sources = observation.Sources
                });
                changed = true;
            }

            if (observation.Sources.Any(s => s.HoldsDefaultSource))
            {
                foreach (var record in updated)
                {
                    if (record.Family == observation.Family
                        && record.IdentityKey != observation.IdentityKey
                        && record.Sources.Any(s => s.HoldsDefaultSource))
                    {
                        foreach (var source in record.Sources) source.HoldsDefaultSource = false;
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            _records = updated;
            Persist();
        }
        return _records;
    }

    /// <summary>The resolved card title for a card id, or null when the card has no account record.</summary>
    public string? ResolvedDisplayName(string cardId) => _records.FirstOrDefault(r => r.Id == cardId)?.ResolvedDisplayName;

    public Dictionary<string, string> ResolvedDisplayNamesByCardId() =>
        _records.ToDictionary(r => r.Id, r => r.ResolvedDisplayName);

    /// <summary>Stores a user rename for a card; null or blank clears it back to the derived name.</summary>
    public void Rename(string cardId, string? name)
    {
        var record = _records.FirstOrDefault(r => r.Id == cardId);
        if (record is null) return;
        var trimmed = name?.Trim().NilIfEmpty();
        if (record.CustomLabel == trimmed) return;
        record.CustomLabel = trimmed;
        Persist();
    }

    public ProviderAccountRecord? DefaultBadgeHolder(string family) =>
        _records.FirstOrDefault(r => r.Family == family && !r.RemovedTombstone && r.Sources.Any(s => s.HoldsDefaultSource));

    /// <summary>The bare family id when free, else an identity-derived "family@hash8" id. Only an
    /// account observed at the family's DEFAULT home may claim the bare id.</summary>
    private static string AvailableId(AccountObservation observation, List<ProviderAccountRecord> records)
    {
        var observedAtDefaultHome = observation.Sources.Any(s => s.Kind == ProviderAccountSource.SourceKind.DefaultHome);
        if (observedAtDefaultHome && !records.Any(r => r.Id == observation.Family))
        {
            return observation.Family;
        }
        var derived = ProviderAccountID.Make(observation.Family, observation.IdentityKey);
        if (!records.Any(r => r.Id == derived)) return derived;

        var attempt = 0;
        while (true)
        {
            var salted = ProviderAccountID.Make(observation.Family, $"{observation.IdentityKey}|{attempt}");
            if (!records.Any(r => r.Id == salted)) return salted;
            attempt++;
        }
    }

    private static bool SourcesEqual(List<ProviderAccountSource> a, List<ProviderAccountSource> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != b[i].Kind || a[i].Anchor != b[i].Anchor
                || a[i].HoldsDefaultSource != b[i].HoldsDefaultSource || a[i].KeychainLiteral != b[i].KeychainLiteral)
            {
                return false;
            }
        }
        return true;
    }

    private void Persist()
    {
        try
        {
            _settings.SetString(StorageKey, JsonSerializer.Serialize(_records));
        }
        catch (Exception ex)
        {
            AppLog.Error(LogTag.Config, $"failed to encode provider-account records; keeping previous persisted state: {ex.Message}");
        }
    }
}
