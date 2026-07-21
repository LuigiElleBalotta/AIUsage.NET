using System.Text.Json;
using AIUsage.Core.Services;

namespace AIUsage.Core.Providers.Devin;

public sealed record DevinAuth(string ApiKey, string? ApiServerUrl = null);

public enum DevinAuthErrorKind
{
    NotLoggedIn
}

public sealed class DevinAuthError : Exception, Models.ICategorizedError
{
    public DevinAuthErrorKind Kind { get; }

    public DevinAuthError(DevinAuthErrorKind kind) : base("Run devin auth login or sign in to Devin and try again.")
    {
        Kind = kind;
    }

    public Models.ErrorCategory ErrorCategory => Models.ErrorCategory.NotLoggedIn;
}

/// <summary>
/// Reads Devin/Windsurf credentials. The credentials.toml lives under the XDG-style data dir
/// (%LOCALAPPDATA%\devin\credentials.toml on Windows, the direct analog of
/// ~/.local/share/devin/credentials.toml). The app auth state lives in the Devin/Windsurf editor's
/// `state.vscdb`, read via Microsoft.Data.Sqlite instead of the macOS `sqlite3` CLI.
/// </summary>
public sealed class DevinAuthStore
{
    public static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "devin", "credentials.toml");
    public static readonly string StateDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Devin", "User", "globalStorage", "state.vscdb");
    public const string DefaultApiServerUrl = "https://server.codeium.com";

    private readonly ITextFileAccessing _files;
    private readonly ISqliteAccessing _sqlite;

    public DevinAuthStore(ITextFileAccessing? files = null, ISqliteAccessing? sqlite = null)
    {
        _files = files ?? new LocalTextFileAccessor();
        _sqlite = sqlite ?? new SqliteDataAccessor();
    }

    public DevinAuth? LoadCredentialsFile()
    {
        if (!_files.Exists(CredentialsPath)) return null;
        string text;
        try { text = _files.ReadText(CredentialsPath); } catch { return null; }
        var apiKey = ReadTomlString(text, "windsurf_api_key");
        if (apiKey is null) return null;
        return new DevinAuth(apiKey, CleanApiServerUrl(ReadTomlString(text, "api_server_url")));
    }

    public DevinAuth? LoadAppAuth()
    {
        const string sql = "SELECT value FROM ItemTable WHERE key = 'windsurfAuthStatus' LIMIT 1";
        string? value;
        try { value = _sqlite.QueryValue(StateDbPath, sql); } catch { return null; }
        if (value is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (!doc.RootElement.TryGetProperty("apiKey", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) return null;
            var apiKey = keyEl.GetString()?.Trim();
            return string.IsNullOrEmpty(apiKey) ? null : new DevinAuth(apiKey);
        }
        catch
        {
            return null;
        }
    }

    public string EffectiveApiServerUrl(DevinAuth auth) => auth.ApiServerUrl ?? DefaultApiServerUrl;

    public static string? CleanApiServerUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("https://", StringComparison.Ordinal)) return null;
        var withoutSlashes = trimmed.TrimEnd('/');
        return withoutSlashes.Length == 0 ? null : withoutSlashes;
    }

    public static string? ReadTomlString(string text, string key)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (line[..eq].Trim() != key) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length == 0) return null;

            if (value[0] is '"' or '\'')
            {
                return ReadQuotedTomlString(value);
            }

            var comment = value.IndexOf('#');
            if (comment >= 0) value = value[..comment].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    private static string? ReadQuotedTomlString(string value)
    {
        var quote = value[0];
        var output = new System.Text.StringBuilder();
        char? previous = null;
        foreach (var c in value.AsSpan(1))
        {
            if (c == quote && previous != '\\')
            {
                var trimmed = output.ToString().Trim();
                return trimmed.Length == 0 ? null : trimmed;
            }
            output.Append(c);
            previous = c;
        }
        return null;
    }
}
