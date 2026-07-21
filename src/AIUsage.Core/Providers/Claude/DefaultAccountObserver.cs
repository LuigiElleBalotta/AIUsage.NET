using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Claude;

/// <summary>
/// Reads which account is signed in at Claude's DEFAULT home — the proven identity slice of the
/// account-first model, with no candidate scanning. An account that can't name itself is reported
/// unresolved, never guessed: identity keys only ever come from Claude Code's own account metadata.
/// Direct port of the Swift DefaultAccountObserver, Claude-only (Codex multi-account is
/// Swift-original scope, not requested for this port — see PORTING_NOTES.md).
/// </summary>
public sealed class DefaultAccountObserver
{
    /// <summary>One family's default-home read this launch.</summary>
    public abstract record Outcome
    {
        /// <summary>The default home named its account.</summary>
        public sealed record Resolved(string IdentityKey, string? Label, string Anchor) : Outcome;
        /// <summary>A credential footprint exists but nothing names the account.</summary>
        public sealed record Unresolved(string Reason) : Outcome;
        /// <summary>No sign of a login at the default home.</summary>
        public sealed record Absent : Outcome;
    }

    /// <summary>Claude Code's per-install state file, which names the signed-in account (oauthAccount).</summary>
    public sealed class ClaudeStateFile
    {
        public sealed class OAuthAccountData
        {
            [JsonPropertyName("accountUuid")] public string? AccountUuid { get; set; }
            [JsonPropertyName("emailAddress")] public string? EmailAddress { get; set; }
            [JsonPropertyName("organizationUuid")] public string? OrganizationUuid { get; set; }
            [JsonPropertyName("organizationName")] public string? OrganizationName { get; set; }
        }

        [JsonPropertyName("oauthAccount")] public OAuthAccountData? OAuthAccount { get; set; }
    }

    private readonly IEnvironmentReading _environment;
    private readonly ITextFileAccessing _files;
    private readonly Func<string> _homeDirectory;

    public DefaultAccountObserver(
        IEnvironmentReading? environment = null,
        ITextFileAccessing? files = null,
        Func<string>? homeDirectory = null)
    {
        _environment = environment ?? new ProcessEnvironmentReader();
        _files = files ?? new LocalTextFileAccessor();
        _homeDirectory = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Claude identity key: account UUID plus the org UUID when present. Plans are
    /// org-scoped — one human commonly has a personal Max org and a company Team org under the SAME
    /// account, and those are different usage pools that must stay different accounts, never merge.</summary>
    public static string? ClaudeIdentityKey(ClaudeStateFile.OAuthAccountData account)
    {
        var uuid = account.AccountUuid?.NilIfEmpty()?.ToLowerInvariant();
        if (uuid is null) return null;
        var org = account.OrganizationUuid?.NilIfEmpty()?.ToLowerInvariant();
        return org is null ? uuid : $"{uuid}|{org}";
    }

    /// <summary>"email (Org Name)" when both are known — the org is what tells two same-email logins apart.</summary>
    public static string? ClaudeIdentityLabel(ClaudeStateFile.OAuthAccountData account)
    {
        var email = account.EmailAddress?.NilIfEmpty();
        var org = account.OrganizationName?.NilIfEmpty();
        if (org is null) return email;
        return email is not null ? $"{email} ({org})" : org;
    }

    private string ExpandTilde(string path)
    {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal)) return path;
        return _homeDirectory() + path[1..].Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>The default Claude home, mirroring ClaudeAuthStore's resolution exactly (the observer
    /// must name the account whose credentials the provider actually refreshes with): CLAUDE_CONFIG_DIR
    /// when exported, else ~/.claude. A comma-separated list can't be assigned one identity.</summary>
    public Outcome ObserveClaude()
    {
        var configDir = "~/.claude";
        var raw = _environment.Value("CLAUDE_CONFIG_DIR")?.Trim().NilIfEmpty();
        if (raw is not null)
        {
            if (raw.Contains(',')) return new Outcome.Unresolved("CLAUDE_CONFIG_DIR is a comma-separated list");
            configDir = raw;
        }
        var anchor = ExpandTilde(configDir);
        // The identity file sits inside a custom config dir, but next to (not inside) the default
        // ~/.claude — Claude Code keeps the default's state at ~/.claude.json.
        var defaultAnchor = ExpandTilde("~/.claude");
        var identityPath = string.Equals(anchor, defaultAnchor, StringComparison.OrdinalIgnoreCase)
            ? ExpandTilde("~/.claude.json")
            : Path.Combine(anchor, ".claude.json");

        string? text;
        try
        {
            text = _files.ReadTextIfPresent(identityPath);
        }
        catch (Exception ex)
        {
            return new Outcome.Unresolved($"identity file unreadable: {ex.Message}");
        }

        if (text is null)
        {
            // No state file. A credential file without it can't be attributed; no footprint = absent.
            return _files.Exists(Path.Combine(anchor, ".credentials.json"))
                ? new Outcome.Unresolved("credentials present but no identity file")
                : new Outcome.Absent();
        }

        ClaudeStateFile? parsed;
        try { parsed = JsonSerializer.Deserialize<ClaudeStateFile>(text); } catch { parsed = null; }
        if (parsed?.OAuthAccount is not { } account || ClaudeIdentityKey(account) is not { } key)
        {
            return new Outcome.Unresolved("identity file present but names no account");
        }
        return new Outcome.Resolved(key, ClaudeIdentityLabel(account), anchor);
    }
}
