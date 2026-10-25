using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

public enum OpenCodeUsageErrorKind
{
    NotLoggedIn,
    CredentialsUnreadable,
    CredentialsExpired,
    CredentialOriginRejected,
    DatabaseUnreadable,
    UnsupportedSchema
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
        OpenCodeUsageErrorKind.CredentialsUnreadable => "Couldn't read OpenCode's saved credentials. Check their file permissions or sign into OpenCode again.",
        OpenCodeUsageErrorKind.CredentialsExpired => "OpenCode's saved sign-in has expired. Run OpenCode to sign in again; local usage is still shown.",
        OpenCodeUsageErrorKind.CredentialOriginRejected => "OpenCode's saved credential points to an unsupported server. Run OpenCode to sign in again.",
        OpenCodeUsageErrorKind.DatabaseUnreadable => "Couldn't read OpenCode's local database. Quit OpenCode and refresh, or check the data directory's permissions.",
        OpenCodeUsageErrorKind.UnsupportedSchema => "OpenCode's local database format isn't recognized. Update OpenCode or AIUsage.NET.",
        _ => "OpenCode usage error."
    };

    public Models.ErrorCategory ErrorCategory => Kind switch
    {
        OpenCodeUsageErrorKind.NotLoggedIn => Models.ErrorCategory.NotLoggedIn,
        OpenCodeUsageErrorKind.CredentialsUnreadable => Models.ErrorCategory.CredentialAccess,
        OpenCodeUsageErrorKind.CredentialsExpired => Models.ErrorCategory.AuthExpired,
        OpenCodeUsageErrorKind.CredentialOriginRejected => Models.ErrorCategory.AuthInvalid,
        OpenCodeUsageErrorKind.DatabaseUnreadable => Models.ErrorCategory.CredentialAccess,
        OpenCodeUsageErrorKind.UnsupportedSchema => Models.ErrorCategory.Decoding,
        _ => Models.ErrorCategory.Other
    };
}

/// <summary>
/// Reads the OpenCode credentials already on the machine. Two on-disk generations are supported:
/// V1's <c>auth.json</c> (<c>opencode-go</c> API key plus an <c>opencode</c> Console entry when
/// present) and V2's <c>credential</c> SQLite table (<c>integration_id='opencode'</c>, active rows).
/// Credentials are only read: nothing is refreshed, rotated, or written, so a scan can never sign the
/// user out. The V2 store wins when it has an active entry; V1 is used only as a fallback.
/// </summary>
public sealed class OpenCodeAuthStore
{
    private readonly ITextFileAccessing _files;
    private readonly IEnvironmentReading _environment;
    private readonly Func<string> _homeDirectory;
    private readonly ISqliteAccessing _sqlite;
    private readonly Func<List<string>> _databasePaths;
    private readonly Func<DateTimeOffset> _now;

    public OpenCodeAuthStore(
        ITextFileAccessing? files = null,
        IEnvironmentReading? environment = null,
        Func<string>? homeDirectory = null,
        ISqliteAccessing? sqlite = null,
        Func<List<string>>? databasePaths = null,
        Func<DateTimeOffset>? now = null)
    {
        _files = files ?? new LocalTextFileAccessor();
        _environment = environment ?? new ProcessEnvironmentReader();
        _homeDirectory = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _sqlite = sqlite ?? new SqliteDataAccessor();
        _databasePaths = databasePaths ?? (() => OpenCodePaths.DatabaseFiles(DataDirectory));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string DataDirectory => OpenCodePaths.DataDirectory(_environment, _homeDirectory());
    public string AuthFilePath => OpenCodePaths.AuthFilePath(DataDirectory);

    /// <summary>V1's Go API key. Kept as a public entry point for first-run detection and compatibility.</summary>
    public string? GoApiKey()
    {
        using var doc = ReadAuthJson();
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!doc.RootElement.TryGetProperty("opencode-go", out var entry) || entry.ValueKind != JsonValueKind.Object) return null;
        if (!entry.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) return null;
        return keyEl.GetString()?.Trim().NilIfEmpty();
    }

    public bool HasAnyLocalCredential()
    {
        if (GoApiKey() is not null) return true;
        return LoadConsoleCredential().Status != OpenCodeCredentialStatus.None;
    }

    /// <summary>
    /// Selects the current Console credential determinedly: active V2 rows first (newest
    /// <c>time_updated</c>), otherwise the V1 <c>opencode</c> entry. An expired entry is still
    /// returned (as <see cref="OpenCodeCredentialStatus.Expired"/>) so the UI can say "reconnect"
    /// without borrowing a credential from another integration or workspace.
    /// </summary>
    public OpenCodeCredentialState LoadConsoleCredential()
    {
        var v2 = LoadV2ConsoleCredential();
        return v2.Status == OpenCodeCredentialStatus.None ? LoadV1ConsoleCredential() : v2;
    }

    private JsonDocument? ReadAuthJson()
    {
        string? text;
        try
        {
            text = _files.ReadTextIfPresent(AuthFilePath);
        }
        catch (Exception ex)
        {
            throw new OpenCodeUsageError(OpenCodeUsageErrorKind.CredentialsUnreadable, ex.Message);
        }
        if (text is null) return null;

        try
        {
            return JsonDocument.Parse(text);
        }
        catch
        {
            throw new OpenCodeUsageError(OpenCodeUsageErrorKind.CredentialsUnreadable, "auth.json is not valid JSON");
        }
    }

    private OpenCodeCredentialState LoadV1ConsoleCredential()
    {
        JsonDocument? doc;
        try
        {
            doc = ReadAuthJson();
        }
        catch (OpenCodeUsageError ex)
        {
            return new OpenCodeCredentialState(OpenCodeCredentialStatus.Unreadable, Detail: ex.Detail);
        }
        catch (Exception)
        {
            return new OpenCodeCredentialState(OpenCodeCredentialStatus.Unreadable, Detail: "credential file unreadable");
        }

        if (doc is null) return OpenCodeCredentialState.Empty;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return OpenCodeCredentialState.Empty;
            if (!doc.RootElement.TryGetProperty("opencode", out var entry) || entry.ValueKind != JsonValueKind.Object)
            {
                return OpenCodeCredentialState.Empty;
            }

            if (!OpenCodeCredentialParser.TryParse(entry, _now(), out var credential, out var failure))
            {
                return new OpenCodeCredentialState(failure, Detail: FailureDetail(failure));
            }
            return new OpenCodeCredentialState(
                credential!.IsExpired(_now()) ? OpenCodeCredentialStatus.Expired : OpenCodeCredentialStatus.Usable,
                credential);
        }
    }

    private sealed record StoredRow(double Updated, string Id, string ValueText);

    private OpenCodeCredentialState LoadV2ConsoleCredential()
    {
        List<string> paths;
        try
        {
            paths = _databasePaths();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Plugin("opencode"), $"credential store: database list unreadable: {ex.Message}");
            return new OpenCodeCredentialState(OpenCodeCredentialStatus.Unreadable, Detail: "database list unreadable");
        }

        var rows = new List<StoredRow>();
        var readFailure = false;
        var malformedRow = false;

        foreach (var path in paths)
        {
            HashSet<string> tables;
            try
            {
                tables = OpenCodeDatabaseSchema.Tables(_sqlite.QueryValue(path, OpenCodeDatabaseSchema.TableNamesSql));
            }
            catch (Exception ex)
            {
                readFailure = true;
                AppLog.Warn(LogTag.Plugin("opencode"), $"credential store schema probe failed for {path}: {ex.Message}");
                continue;
            }
            if (!tables.Contains("credential")) continue;

            string? payload;
            try
            {
                payload = _sqlite.QueryValue(path, CredentialRowsSql);
            }
            catch (Exception ex)
            {
                readFailure = true;
                AppLog.Warn(LogTag.Plugin("opencode"), $"credential query failed for {path}: {ex.Message}");
                continue;
            }
            if (payload is null) continue;

            foreach (var row in payload.Split('\u001f', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryReadStoredRow(row, out var stored))
                {
                    malformedRow = true;
                    AppLog.Warn(LogTag.Plugin("opencode"), $"credential store row malformed in {path}");
                    continue;
                }
                rows.Add(stored!);
            }
        }

        if (readFailure)
        {
            // A failed store read must stay visible even when another database returned rows:
            // we cannot know whether the unreadable database holds a newer active credential,
            // so using a row from another database or falling back to V1 could borrow the wrong workspace.
            return new OpenCodeCredentialState(OpenCodeCredentialStatus.Unreadable, Detail: "credential store unreadable");
        }
        if (malformedRow)
        {
            return new OpenCodeCredentialState(OpenCodeCredentialStatus.Invalid, Detail: "credential row malformed");
        }

        if (rows.Count > 0)
        {
            rows.Sort((a, b) =>
            {
                var updated = b.Updated.CompareTo(a.Updated);
                return updated != 0 ? updated : string.CompareOrdinal(b.Id, a.Id);
            });

            // Pick the newest active row first, then validate only that row. If it is expired,
            // invalid, or points at an untrusted origin, report that state: an older row may belong
            // to another workspace and must never be borrowed.
            var newest = rows[0];
            TryParseCredentialValue(newest.ValueText, out var credential, out var status);
            if (credential is null)
            {
                return new OpenCodeCredentialState(status, Detail: FailureDetail(status));
            }
            return new OpenCodeCredentialState(status, credential);
        }

        return OpenCodeCredentialState.Empty;
    }

    private static readonly string CredentialRowsSql = $"""
        SELECT group_concat(json_object('value', value, 'updated', time_updated, 'id', id), char(31))
        FROM (
            SELECT value, time_updated, id
            FROM credential
            WHERE integration_id = 'opencode' AND active = 1
            ORDER BY time_updated DESC, id DESC
        );
        """;

    private static bool TryReadStoredRow(string row, out StoredRow? stored)
    {
        stored = null;
        try
        {
            using var wrapper = JsonDocument.Parse(row);
            if (wrapper.RootElement.ValueKind != JsonValueKind.Object) return false;

            var updatedElement = OpenCodeCredentialParser.Property(wrapper.RootElement, "updated");
            if (ProviderParse.Number(updatedElement) is not { } updated || !double.IsFinite(updated))
            {
                return false;
            }

            var id = OpenCodeCredentialParser.StringProperty(wrapper.RootElement, "id")?.Trim().NilIfEmpty();
            if (id is null) return false;

            // Keep a row with a missing/empty value when its timestamp and id are valid: its timestamp
            // is still needed so the newest active row can be reported as invalid instead of silently borrowing an older one.
            var valueElement = OpenCodeCredentialParser.Property(wrapper.RootElement, "value");
            if (valueElement is { ValueKind: not JsonValueKind.String and not JsonValueKind.Null })
            {
                return false;
            }
            var valueText = valueElement is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";

            stored = new StoredRow(updated, id, valueText);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryParseCredentialValue(
        string valueText,
        out OpenCodeCredential? credential,
        out OpenCodeCredentialStatus status)
    {
        credential = null;
        status = OpenCodeCredentialStatus.Invalid;
        try
        {
            using var value = JsonDocument.Parse(valueText);
            return OpenCodeCredentialParser.TryParse(value.RootElement, _now(), out credential, out status);
        }
        catch
        {
            return false;
        }
    }

    private static string FailureDetail(OpenCodeCredentialStatus status) => status switch
    {
        OpenCodeCredentialStatus.Expired => "credential expired",
        OpenCodeCredentialStatus.UntrustedOrigin => "credential origin rejected",
        OpenCodeCredentialStatus.Unreadable => "credential unreadable",
        _ => "credential data invalid"
    };
}
