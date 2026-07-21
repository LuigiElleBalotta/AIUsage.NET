using System.Text.RegularExpressions;
using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.Copilot;

public sealed record CopilotToken(string Value);

public enum CopilotAuthErrorKind
{
    NotLoggedIn,
    TokenInvalid
}

public sealed class CopilotAuthError : Exception, Models.ICategorizedError
{
    public CopilotAuthErrorKind Kind { get; }

    public CopilotAuthError(CopilotAuthErrorKind kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    private static string Describe(CopilotAuthErrorKind kind) => kind switch
    {
        CopilotAuthErrorKind.NotLoggedIn => "Sign in to GitHub Copilot in your editor, or run gh auth login, and try again.",
        CopilotAuthErrorKind.TokenInvalid => "GitHub token invalid or expired. Re-authenticate (gh auth login) and try again.",
        _ => "Copilot authentication error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        CopilotAuthErrorKind.NotLoggedIn => Models.ErrorCategory.NotLoggedIn,
        CopilotAuthErrorKind.TokenInvalid => Models.ErrorCategory.AuthExpired,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>
/// Reads a GitHub token Copilot tooling already left on the machine. Windows paths mirror the macOS
/// ones (Copilot editor plugins and `gh` use the same `%USERPROFILE%\.config\...` layout cross-platform
/// via Go/Node's config resolution). Windows Credential Manager is probed as the `gh` keychain
/// equivalent (go-keyring wraps secrets identically on both OSes).
/// </summary>
public sealed class CopilotAuthStore
{
    public const string EditorAppsPath = "~/.config/github-copilot/apps.json";
    public const string EditorHostsPath = "~/.config/github-copilot/hosts.json";
    public const string GhHostsPath = "~/.config/gh/hosts.yml";
    public const string GhKeychainService = "gh:github.com";

    private readonly ITextFileAccessing _files;
    private readonly IKeychainAccessing _keychain;

    public CopilotAuthStore(ITextFileAccessing? files = null, IKeychainAccessing? keychain = null)
    {
        _files = files ?? new LocalTextFileAccessor();
        _keychain = keychain ?? new WindowsCredentialAccessor();
    }

    public CopilotToken? LoadToken() => LoadFromEditorConfig() ?? LoadFromGhConfig() ?? LoadFromGhKeychain();

    public CopilotToken? LoadFromEditorConfig()
    {
        foreach (var path in new[] { EditorAppsPath, EditorHostsPath })
        {
            if (!_files.Exists(path)) continue;
            string text;
            try { text = _files.ReadText(path); } catch { continue; }
            if (OauthTokenFromEditorJson(text) is { } token) return new CopilotToken(token);
        }
        return null;
    }

    public CopilotToken? LoadFromGhConfig()
    {
        if (!_files.Exists(GhHostsPath)) return null;
        string text;
        try { text = _files.ReadText(GhHostsPath); } catch { return null; }
        return YamlValue(text, "oauth_token") is { } token ? new CopilotToken(token) : null;
    }

    public CopilotToken? LoadFromGhKeychain()
    {
        var raw = ReadGhKeychainRaw();
        if (raw is null) return null;
        var token = Support.ProviderParse.UnwrapGoKeyring(raw);
        return token is not null ? new CopilotToken(token) : null;
    }

    private string? ReadGhKeychainRaw()
    {
        var account = GhUsername();
        if (account is not null)
        {
            try
            {
                var value = _keychain.ReadGenericPassword(GhKeychainService, account);
                if (value is not null) return value;
            }
            catch { }
        }
        try { return _keychain.ReadGenericPassword(GhKeychainService); } catch { return null; }
    }

    private string? GhUsername()
    {
        if (!_files.Exists(GhHostsPath)) return null;
        string text;
        try { text = _files.ReadText(GhHostsPath); } catch { return null; }
        return YamlValue(text, "user");
    }

    public static string? OauthTokenFromEditorJson(string text)
    {
        System.Text.Json.JsonElement root;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            root = doc.RootElement.Clone();
        }
        catch { return null; }

        string? TokenIn(System.Text.Json.JsonElement value)
        {
            if (value.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!value.TryGetProperty("oauth_token", out var t) || t.ValueKind != System.Text.Json.JsonValueKind.String) return null;
            var token = t.GetString()?.Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "github.com" || prop.Name.StartsWith("github.com:", StringComparison.Ordinal))
            {
                if (TokenIn(prop.Value) is { } token) return token;
            }
        }
        return null;
    }

    public static string? YamlValue(string text, string key, string host = "github.com")
    {
        var prefix = key + ":";
        var hostHeader = host + ":";
        var inHost = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                inHost = line.Trim().StartsWith(hostHeader, StringComparison.Ordinal);
                continue;
            }
            if (!inHost) continue;
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var value = trimmed[prefix.Length..].Trim();
            var unquoted = value.Trim('"', '\'');
            return unquoted.Length == 0 ? null : unquoted;
        }
        return null;
    }
}
