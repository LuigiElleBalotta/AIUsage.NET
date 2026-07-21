using AIUsage.Core.Providers.Claude;
using AIUsage.Core.Services;
using AIUsage.Core.Stores;
using AIUsage.Core.Support;

namespace AIUsage.Core.App;

/// <summary>
/// One extra Claude account card to build this launch: a custom-config-dir login found on this
/// computer whose account is distinct from the default card's. Cards render only while their source
/// is found this launch — a record with no finding this launch simply builds no card. Direct port of
/// the Swift ClaudeAccountCard.
/// </summary>
public sealed record ClaudeAccountCard(
    /// <summary>The account's stable record id ("claude@ab12cd34") — the card id everywhere:
    /// layout, cache, CLI/API matching.</summary>
    string Id,
    /// <summary>The DERIVED card name baked into the launch Provider. Never a rename — renames live
    /// only in the account registry and are resolved at render time.</summary>
    string DisplayName,
    /// <summary>The config dir the card's credentials and spend logs are pinned to.</summary>
    string ConfigDirPath,
    /// <summary>The literal string whose hash names the dir's Credential Manager item.</summary>
    string KeychainLiteral,
    /// <summary>Same-account additional config dirs (rare): extra spend-log roots, never extra credentials.</summary>
    List<string>? ExtraLogRoots = null);

/// <summary>
/// The launch-time account pass: read which account is signed in at Claude's default home, scan for
/// extra Claude logins in custom config dirs, reconcile the account registry, and expose the extra-
/// card build plan <see cref="ProviderCatalog"/> consumes. Direct port of the Swift
/// ProviderAccountAssembly, Claude-only (Codex multi-account is Swift-original scope, not requested
/// for this port — see PORTING_NOTES.md) and without the login-shell-readiness gating: a Windows
/// process launched from Explorer/Start Menu always inherits persisted user/machine env vars, so the
/// "shell facts unreadable on a cold Finder/Dock launch" concern the Swift edition guards against has
/// no Windows equivalent.
/// </summary>
public sealed class ProviderAccountAssembly
{
    /// <summary>Card id -> the account identity signed in there this launch. A card whose identity
    /// didn't resolve is absent.</summary>
    public Dictionary<string, string> IdentityKeysByCard { get; init; } = new();
    /// <summary>Extra Claude account cards found on this computer this launch, in stable id order.</summary>
    public List<ClaudeAccountCard> ClaudeCards { get; init; } = new();
    /// <summary>Same-account custom config dirs discovered for the DEFAULT card's login: extra
    /// spend-log roots for the default scanner, never extra credentials.</summary>
    public List<string> DefaultClaudeExtraLogRoots { get; init; } = new();

    public static ProviderAccountAssembly Make(ProviderAccountsStore? accountsStore = null) =>
        Make(new DefaultAccountObserver(), accountsStore ?? new ProviderAccountsStore(), new ClaudeConfigDirDiscovery());

    /// <summary>The environment-independent core, separated so tests inject a fixed observer,
    /// discovery, and scratch store.</summary>
    public static ProviderAccountAssembly Make(
        DefaultAccountObserver observer,
        ProviderAccountsStore accountsStore,
        ClaudeConfigDirDiscovery? claudeDiscovery = null)
    {
        var identityKeys = new Dictionary<string, string>();
        var observations = new List<ProviderAccountsStore.AccountObservation>();

        var claudeOutcome = observer.ObserveClaude();
        switch (claudeOutcome)
        {
            case DefaultAccountObserver.Outcome.Resolved resolved:
                identityKeys["claude"] = resolved.IdentityKey;
                observations.Add(new ProviderAccountsStore.AccountObservation
                {
                    Family = "claude",
                    IdentityKey = resolved.IdentityKey,
                    Label = resolved.Label,
                    Sources = new List<ProviderAccountSource>
                    {
                        new() { Kind = ProviderAccountSource.SourceKind.DefaultHome, Anchor = resolved.Anchor, HoldsDefaultSource = true }
                    }
                });
                AppLog.Info(LogTag.Config, $"accounts: claude default identity resolved ({ProviderAccountID.Make("claude", resolved.IdentityKey)})");
                break;
            case DefaultAccountObserver.Outcome.Unresolved unresolved:
                AppLog.Info(LogTag.Config, $"accounts: claude default identity unresolved — {unresolved.Reason}");
                break;
            case DefaultAccountObserver.Outcome.Absent:
                AppLog.Debug(LogTag.Config, "accounts: claude has no default login");
                break;
        }

        // Extra Claude logins in custom config dirs. Guarded on the default read: when a default
        // login clearly EXISTS but can't be named (unresolved), accepting candidates could render
        // the very account the default card shows as a second card — skip them this launch instead.
        var foundClaudeAccounts = new List<(string IdentityKey, string? Label, List<ClaudeConfigDirDiscovery.Finding> Dirs)>();
        var defaultClaudeExtraLogRoots = new List<string>();

        if (claudeDiscovery is not null)
        {
            if (claudeOutcome is DefaultAccountObserver.Outcome.Unresolved)
            {
                AppLog.Info(LogTag.Config, "discovery: claude default login present but its identity is unreadable — skipping extra-account candidates this launch");
            }
            else
            {
                var defaultKey = identityKeys.GetValueOrDefault("claude");
                var scan = claudeDiscovery.Run();
                foreach (var note in scan.Notes) AppLog.Info(LogTag.Config, $"discovery: {note}");

                var order = new List<string>();
                var grouped = new Dictionary<string, List<ClaudeConfigDirDiscovery.Finding>>();
                foreach (var finding in scan.Findings)
                {
                    if (!grouped.ContainsKey(finding.IdentityKey))
                    {
                        order.Add(finding.IdentityKey);
                        grouped[finding.IdentityKey] = new List<ClaudeConfigDirDiscovery.Finding>();
                    }
                    grouped[finding.IdentityKey].Add(finding);
                }

                foreach (var identityKey in order)
                {
                    var findings = grouped[identityKey];
                    var sources = findings.Select(f => new ProviderAccountSource
                    {
                        Kind = ProviderAccountSource.SourceKind.ConfigDir,
                        Anchor = f.AnchorPath,
                        HoldsDefaultSource = false,
                        KeychainLiteral = f.KeychainLiteral
                    }).ToList();

                    if (identityKey == defaultKey)
                    {
                        // Same account as the default card: its dirs are extra spend-log roots on
                        // that card, never a second card.
                        defaultClaudeExtraLogRoots.AddRange(findings.Select(f => f.AnchorPath));
                        var existing = observations.FirstOrDefault(o => o.Family == "claude" && o.IdentityKey == identityKey);
                        existing?.Sources.AddRange(sources);
                        AppLog.Info(LogTag.Config, $"discovery: {findings.Count} config dir(s) fold onto the default claude card (same account)");
                    }
                    else
                    {
                        observations.Add(new ProviderAccountsStore.AccountObservation
                        {
                            Family = "claude",
                            IdentityKey = identityKey,
                            Label = findings[0].Label,
                            Sources = sources
                        });
                        foundClaudeAccounts.Add((identityKey, findings[0].Label, findings));
                    }
                }
            }
        }

        var records = accountsStore.Reconcile(observations);

        var claudeCards = new List<ClaudeAccountCard>();
        foreach (var account in foundClaudeAccounts)
        {
            var record = records.FirstOrDefault(r => r.Family == "claude" && r.IdentityKey == account.IdentityKey);
            if (record is null) continue;
            if (record.Id == "claude")
            {
                // The bare record's account has moved out of the default home into a config dir
                // while another login occupies the default. The bare CARD is the default home's
                // runtime, so this record can't render under its own id this launch.
                AppLog.Warn(LogTag.Config, "discovery: the claude record's account now lives in a config dir; its card is unavailable until swap support lands");
                continue;
            }
            var primary = account.Dirs.FirstOrDefault();
            if (primary is null) continue;
            claudeCards.Add(new ClaudeAccountCard(
                record.Id,
                record.DerivedDisplayName,
                primary.AnchorPath,
                primary.KeychainLiteral,
                account.Dirs.Skip(1).Select(f => f.AnchorPath).ToList()));
            identityKeys[record.Id] = account.IdentityKey;
            AppLog.Info(LogTag.Config, $"accounts: extra claude card {record.Id} from {account.Dirs.Count} config dir(s)");
        }
        claudeCards.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        return new ProviderAccountAssembly
        {
            IdentityKeysByCard = identityKeys,
            ClaudeCards = claudeCards,
            DefaultClaudeExtraLogRoots = defaultClaudeExtraLogRoots
        };
    }
}
