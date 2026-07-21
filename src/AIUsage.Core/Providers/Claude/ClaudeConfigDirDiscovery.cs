using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Stores;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Claude;

/// <summary>
/// Launch-time scan for EXTRA Claude logins in custom config dirs — the homes a user points
/// CLAUDE_CONFIG_DIR at besides the default (~/.claude). Direct port of the Swift
/// ClaudeConfigDirDiscovery.
///
/// Runs synchronously inside the launch account pass under a small time budget, and reads NO
/// keychain secrets — credential presence is checked from file existence and attributes-only
/// Credential Manager probes, so discovery can never raise a credential prompt or block launch.
///
/// Shape rules: candidates are dot-dirs at ~ and dirs under ~/.config — bounded, never temp dirs or
/// project trees. A candidate only counts when it carries Claude's exact credential shape AND names
/// its account (identity read from the home itself).
/// </summary>
public sealed class ClaudeConfigDirDiscovery
{
    /// <summary>One accepted custom-config-dir login. Whether it becomes its own card or attaches to
    /// an existing account's record is the assembly's call, not discovery's.</summary>
    public sealed record Finding(string IdentityKey, string? Label, string AnchorPath, string KeychainLiteral);

    public sealed class Result
    {
        public List<Finding> Findings { get; } = new();
        /// <summary>The support trail: one line per notable decision (near-miss rejections, folds),
        /// emitted to the log so a "my account didn't show up" report is diagnosable from a default
        /// log. Token-free and email-free by construction — identity hashes, kinds, and paths only.</summary>
        public List<string> Notes { get; } = new();
    }

    private readonly IEnvironmentReading _environment;
    private readonly ITextFileAccessing _files;
    private readonly IKeychainAccessing _keychain;
    private readonly Func<string> _homeDirectory;
    private readonly Func<string, List<string>> _listSubdirectories;
    private readonly TimeSpan _timeBudget;
    private readonly Func<DateTimeOffset> _now;

    public ClaudeConfigDirDiscovery(
        IEnvironmentReading? environment = null,
        ITextFileAccessing? files = null,
        IKeychainAccessing? keychain = null,
        Func<string>? homeDirectory = null,
        Func<string, List<string>>? listSubdirectories = null,
        TimeSpan? timeBudget = null,
        Func<DateTimeOffset>? now = null)
    {
        _environment = environment ?? new ProcessEnvironmentReader();
        _files = files ?? new LocalTextFileAccessor();
        _keychain = keychain ?? new WindowsCredentialAccessor();
        _homeDirectory = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _listSubdirectories = listSubdirectories ?? FilesystemSubdirectories;
        _timeBudget = timeBudget ?? TimeSpan.FromMilliseconds(400);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public Result Run()
    {
        var started = _now();
        var result = new Result();
        var excluded = new HashSet<string>(DefaultClaudeConfigDirs().Select(Canonical), StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in CandidateDirectories())
        {
            if (_now() - started > _timeBudget)
            {
                result.Notes.Add($"claude config-dir scan hit its {(int)_timeBudget.TotalMilliseconds}ms budget; finishing with partial results");
                break;
            }
            if (excluded.Contains(Canonical(candidate))) continue;
            var finding = ClaudeCandidate(candidate, result.Notes);
            if (finding is not null) result.Findings.Add(finding);
        }
        return result;
    }

    // MARK: - Candidates

    /// <summary>Dot-dirs at ~ plus dirs under ~/.config, in stable path order.</summary>
    private List<string> CandidateDirectories()
    {
        var home = _homeDirectory();
        var candidates = _listSubdirectories(home).Where(d => Path.GetFileName(d).StartsWith('.')).ToList();
        candidates.AddRange(_listSubdirectories(Path.Combine(home, ".config")));
        candidates.Sort(StringComparer.Ordinal);
        return candidates;
    }

    private static List<string> FilesystemSubdirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path).ToList() : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private Finding? ClaudeCandidate(string dirPath, List<string> notes)
    {
        // Pre-gate: only dirs that carry an identity file at all enter the trail — everything else
        // is a random dot-dir and stays out of the log. (A custom config dir keeps its state INSIDE
        // the dir; only the default ~/.claude keeps it next door at ~/.claude.json, and the default
        // homes are excluded before this runs.)
        string? identityText;
        try { identityText = _files.ReadTextIfPresent(Path.Combine(dirPath, ".claude.json")); }
        catch { return null; }
        if (identityText is null) return null;

        DefaultAccountObserver.ClaudeStateFile? parsed;
        try { parsed = JsonSerializer.Deserialize<DefaultAccountObserver.ClaudeStateFile>(identityText); }
        catch { parsed = null; }
        if (parsed?.OAuthAccount is not { } account || DefaultAccountObserver.ClaudeIdentityKey(account) is not { } key)
        {
            notes.Add($"claude candidate {LogPath(dirPath)}: identity file present but names no account → skipped");
            return null;
        }

        // Credential shape: the dir's own .credentials.json, or its *computed* keychain item.
        // Claude Code hashes the literal CLAUDE_CONFIG_DIR string, so every plausible spelling of
        // this path is probed (attributes only — no secret, no prompt).
        var fileBacked = false;
        try
        {
            var credText = _files.ReadTextIfPresent(Path.Combine(dirPath, ".credentials.json"));
            fileBacked = credText is not null
                && ClaudeAuthStore.ParseCredentials(credText)?.ClaudeAiOauth?.AccessToken?.NilIfEmpty() is not null;
        }
        catch { /* treated as not file-backed */ }

        string? matchedLiteral = null;
        var literals = KeychainLiterals(dirPath);
        foreach (var literal in literals)
        {
            var service = ClaudeAuthStore.ScopedKeychainServiceName(literal, _environment);
            if (_keychain.GenericPasswordExists(service) == true)
            {
                matchedLiteral = literal;
                break;
            }
        }

        if (!fileBacked && matchedLiteral is null)
        {
            notes.Add($"claude candidate {LogPath(dirPath)}: identity {Hash8(key)} but no credential (no .credentials.json, no keychain item for {literals.Count} path spellings) → skipped");
            return null;
        }

        notes.Add($"claude candidate {LogPath(dirPath)}: accepted ({Hash8(key)}, {(fileBacked ? "file" : "keychain")} credential)");
        return new Finding(key, DefaultAccountObserver.ClaudeIdentityLabel(account), dirPath, matchedLiteral ?? dirPath);
    }

    /// <summary>Every plausible spelling Claude Code might have hashed for this dir's keychain item:
    /// the path as listed and with the home prefix swapped for "~" (users export
    /// CLAUDE_CONFIG_DIR=~/x and =C:\Users\me\x interchangeably).</summary>
    private List<string> KeychainLiterals(string dirPath)
    {
        var home = _homeDirectory();
        var candidates = new List<string> { dirPath };
        var literals = new List<string>();
        foreach (var candidate in candidates)
        {
            literals.Add(candidate);
            if (candidate.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = candidate[home.Length..].Replace('\\', '/');
                literals.Add("~" + suffix);
            }
        }
        var seen = new HashSet<string>();
        return literals.Where(l => seen.Add(l)).ToList();
    }

    // MARK: - Default homes (the exclusion set)

    /// <summary>The default card's config dirs: CLAUDE_CONFIG_DIR when set, else ~/.claude.</summary>
    private List<string> DefaultClaudeConfigDirs()
    {
        var raw = _environment.Value("CLAUDE_CONFIG_DIR")?.Trim().NilIfEmpty();
        if (raw is not null)
        {
            var dirs = raw.Split(',').Select(d => d.Trim()).Where(d => d.Length > 0).ToList();
            if (dirs.Count > 0) return dirs.Select(ExpandTilde).ToList();
        }
        var home = _homeDirectory();
        return new List<string> { Path.Combine(home, ".claude") };
    }

    // MARK: - Path helpers

    private string ExpandTilde(string path)
    {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith("~\\", StringComparison.Ordinal)) return path;
        return _homeDirectory() + path[1..].Replace('/', Path.DirectorySeparatorChar);
    }

    private string Canonical(string path)
    {
        try { return Path.GetFullPath(ExpandTilde(path)).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return ExpandTilde(path); }
    }

    /// <summary>Log-safe path: the home prefix is folded to "~" so support logs don't carry the username.</summary>
    private string LogPath(string path)
    {
        var home = _homeDirectory();
        return path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..].Replace('\\', '/')
            : path;
    }

    private static string Hash8(string identityKey) => ProviderAccountID.Hash8(identityKey);
}
