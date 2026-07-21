using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

public enum OpenCodeUsageErrorKind
{
    NotLoggedIn,
    CredentialsUnreadable,
    DatabaseUnreadable
}

public sealed class OpenCodeUsageError : Exception, Models.ICategorizedError
{
    public OpenCodeUsageErrorKind Kind { get; }
    public string? Detail { get; }

    public OpenCodeUsageError(OpenCodeUsageErrorKind kind, string? detail = null) : base(Describe(kind))
    {
        Kind = kind;
        Detail = detail;
    }

    private static string Describe(OpenCodeUsageErrorKind kind) => kind switch
    {
        OpenCodeUsageErrorKind.NotLoggedIn => "OpenCode not detected. Log in with OpenCode Go or use OpenCode locally first.",
        OpenCodeUsageErrorKind.CredentialsUnreadable => "Couldn't read OpenCode's auth.json. Check its file permissions or log into OpenCode Go again.",
        OpenCodeUsageErrorKind.DatabaseUnreadable => "Couldn't read OpenCode's local database. Quit OpenCode and refresh, or check the data directory's permissions.",
        _ => "OpenCode usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        OpenCodeUsageErrorKind.NotLoggedIn => Models.ErrorCategory.NotLoggedIn,
        OpenCodeUsageErrorKind.CredentialsUnreadable => Models.ErrorCategory.CredentialAccess,
        OpenCodeUsageErrorKind.DatabaseUnreadable => Models.ErrorCategory.CredentialAccess,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>Reads the OpenCode Go/Zen credential already on the machine. Direct port of OpenCodeAuthStore.</summary>
public sealed class OpenCodeAuthStore
{
    private readonly ITextFileAccessing _files;
    private readonly IEnvironmentReading _environment;
    private readonly Func<string> _homeDirectory;

    public OpenCodeAuthStore(ITextFileAccessing? files = null, IEnvironmentReading? environment = null, Func<string>? homeDirectory = null)
    {
        _files = files ?? new LocalTextFileAccessor();
        _environment = environment ?? new ProcessEnvironmentReader();
        _homeDirectory = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public string DataDirectory => OpenCodePaths.DataDirectory(_environment, _homeDirectory());
    public string AuthFilePath => OpenCodePaths.AuthFilePath(DataDirectory);

    public string? GoApiKey()
    {
        string? text;
        try { text = _files.ReadTextIfPresent(AuthFilePath); }
        catch (Exception ex) { throw new OpenCodeUsageError(OpenCodeUsageErrorKind.CredentialsUnreadable, ex.Message); }
        if (text is null) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch { throw new OpenCodeUsageError(OpenCodeUsageErrorKind.CredentialsUnreadable, "auth.json is not valid JSON"); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("opencode-go", out var entry) || entry.ValueKind != JsonValueKind.Object) return null;
            if (!entry.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) return null;
            return keyEl.GetString()?.Trim().NilIfEmpty();
        }
    }
}
