using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

public sealed record OpenCodeUsageScan(
    LogUsageScan LogScan,
    OpenCodeScanReport Report,
    DateTimeOffset CalendarNow);

/// <summary>Safe, non-secret diagnostics about which local stores were read this refresh.</summary>
public sealed record OpenCodeScanReport(
    int DatabaseCount,
    int ReadableDatabaseCount,
    int FailedDatabaseCount,
    int UnsupportedDatabaseCount,
    bool HasLegacySchema,
    bool HasV2Schema,
    bool HasHostedUsage,
    bool HasGoUsage,
    string? VersionHint,
    bool HasMissingCost);

/// <summary>
/// Reads OpenCode's local SQLite logs and normalizes both on-disk generations:
///
/// * V1 <c>message(id, …, data)</c> — assistant JSON at <c>$.role</c>, <c>$.providerID</c>,
///   <c>$.modelID</c>, <c>$.cost</c>, <c>$.tokens.total</c>.
/// * V2 <c>session_message(id, …, type, …, data)</c> — <c>type='assistant'</c>, JSON at
///   <c>$.model.providerID</c>, <c>$.model.id</c>, <c>$.cost</c>, and separate token counters.
///
/// Migration copies message IDs between the two tables, so rows are deduplicated globally by message
/// ID with V2 winning regardless of timestamp; within one generation the newest <c>time_updated</c>
/// (falling back to <c>time_created</c>) wins, and equal timestamps break on source-path ordinal.
/// Readers are chosen from the tables that actually exist; the installed CLI
/// version is only a diagnostic hint and never a gate. Rows keep their absolute timestamps but are
/// bucketed into local calendar days (with the injected zone's DST rules) for the spend tiles.
/// </summary>
public sealed class OpenCodeUsageScanner
{
    public static readonly string[] HostedProviderIds = { "opencode-go", "opencode" };
    public const string GoProviderId = "opencode-go";

    private readonly ISqliteAccessing _sqlite;
    private readonly Func<List<string>> _databasePaths;
    private readonly OpenCodeVersionProbe? _versionProbe;
    private readonly TimeZoneInfo _timeZone;
    private string? _lastLayoutLog;

    public OpenCodeUsageScanner(
        ISqliteAccessing? sqlite = null,
        Func<List<string>>? databasePaths = null,
        IEnvironmentReading? environment = null,
        Func<string>? homeDirectory = null,
        OpenCodeVersionProbe? versionProbe = null,
        TimeZoneInfo? timeZone = null)
    {
        _sqlite = sqlite ?? new SqliteDataAccessor();
        _timeZone = timeZone ?? TimeZoneInfo.Local;

        if (databasePaths is null)
        {
            var environmentReader = environment ?? new ProcessEnvironmentReader();
            var home = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            databasePaths = () => OpenCodePaths.DatabaseFiles(
                OpenCodePaths.DataDirectory(environmentReader, home()));
        }
        _databasePaths = databasePaths;
        _versionProbe = versionProbe;
    }

    public async Task<OpenCodeUsageScan?> ScanAsync(
        DateTimeOffset now,
        int daysBack = 33,
        CancellationToken cancellationToken = default)
    {
        List<string> paths;
        try
        {
            paths = await Task.Run(_databasePaths, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Plugin("opencode"), $"data directory unreadable: {ex.Message}");
            throw new OpenCodeUsageError(OpenCodeUsageErrorKind.DatabaseUnreadable);
        }
        if (paths.Count == 0) return null;

        // OpenCode spend tiles are calendar-day buckets in the machine's local time, including DST
        // transitions. Query and filter from exact local-midnight boundaries instead of mixing the
        // caller's offset with DateTime.Date (which silently uses the host's local interpretation).
        var calendarNow = TimeZoneInfo.ConvertTime(now, _timeZone);
        var today = DateOnly.FromDateTime(calendarNow.DateTime);
        var querySince = OpenCodeLocalCalendar.Midnight(today.AddDays(-daysBack), _timeZone);
        var cutoffMs = querySince.ToUnixTimeMilliseconds();
        var rowsById = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var anonymousRows = new List<OpenCodeUsageRow>();
        var readable = 0;
        var failed = 0;
        var unsupported = 0;
        var hasLegacySchema = false;
        var hasV2Schema = false;
        var anyUsageTable = false;
        var anyDataQuerySucceeded = false;

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HashSet<string> tables;
            try
            {
                tables = await Task.Run(
                    () => OpenCodeDatabaseSchema.Tables(_sqlite.QueryValue(path, OpenCodeDatabaseSchema.TableNamesSql)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warn(LogTag.Plugin("opencode"), $"schema probe failed for {path}: {ex.Message}");
                continue;
            }
            readable++;

            var legacy = tables.Contains("message");
            var v2 = tables.Contains("session_message");
            hasLegacySchema |= legacy;
            hasV2Schema |= v2;

            if (!legacy && !v2)
            {
                if (!OpenCodeDatabaseSchema.LooksLikeKnownSchema(tables))
                {
                    unsupported++;
                    AppLog.Warn(LogTag.Plugin("opencode"), $"unrecognized database schema in {path}");
                }
                continue;
            }
            anyUsageTable = true;
            var databaseFailed = false;

            if (legacy)
            {
                HashSet<string>? legacyColumns = null;
                try
                {
                    legacyColumns = OpenCodeDatabaseSchema.Tables(
                        _sqlite.QueryValue(path, OpenCodeDatabaseSchema.ColumnNamesSql("message")));
                }
                catch (Exception ex)
                {
                    // Assume the documented V1 shape (time_updated exists), but leave a diagnostic.
                    AppLog.Warn(LogTag.Plugin("opencode"),
                        $"V1 column probe failed for {path}; assuming the time_updated column exists: {ex.Message}");
                }

                try
                {
                    var json = await Task.Run(
                        () => _sqlite.QueryValue(path, OpenCodeUsageQuery.LegacySql(cutoffMs, legacyColumns)),
                        cancellationToken).ConfigureAwait(false);
                    anyDataQuerySucceeded = true;
                    if (json is not null)
                    {
                        var parsed = OpenCodeUsageQuery.TryParseRows(json, isV2: false, out var parsedRows, out var parseError);
                        foreach (var row in parsedRows)
                        {
                            AddRow(row, path);
                        }
                        if (!parsed)
                        {
                            databaseFailed = true;
                            AppLog.Warn(LogTag.Plugin("opencode"), $"legacy usage payload malformed for {path}: {parseError}");
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    databaseFailed = true;
                    AppLog.Warn(LogTag.Plugin("opencode"), $"legacy usage query failed for {path}: {ex.Message}");
                }
            }

            if (v2)
            {
                HashSet<string>? columns = null;
                try
                {
                    columns = OpenCodeDatabaseSchema.Tables(
                        _sqlite.QueryValue(path, OpenCodeDatabaseSchema.ColumnNamesSql("session_message")));
                }
                catch (Exception ex)
                {
                    // Fall back to the documented V2 shape; the reader below only uses this to decide
                    // whether the type column can be referenced. Log the downgrade so a changed schema
                    // cannot hide behind an unavailable column probe.
                    AppLog.Warn(LogTag.Plugin("opencode"),
                        $"V2 column probe failed for {path}; assuming the type column exists: {ex.Message}");
                }

                try
                {
                    var json = await Task.Run(
                        () => _sqlite.QueryValue(path, OpenCodeUsageQuery.V2Sql(cutoffMs, columns)),
                        cancellationToken).ConfigureAwait(false);
                    anyDataQuerySucceeded = true;
                    if (json is not null)
                    {
                        var parsed = OpenCodeUsageQuery.TryParseRows(json, isV2: true, out var parsedRows, out var parseError);
                        foreach (var row in parsedRows)
                        {
                            AddRow(row, path);
                        }
                        if (!parsed)
                        {
                            databaseFailed = true;
                            AppLog.Warn(LogTag.Plugin("opencode"), $"V2 usage payload malformed for {path}: {parseError}");
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    databaseFailed = true;
                    AppLog.Warn(LogTag.Plugin("opencode"), $"V2 usage query failed for {path}: {ex.Message}");
                }
            }

            if (databaseFailed) failed++;
        }

        if (readable == 0)
        {
            throw new OpenCodeUsageError(OpenCodeUsageErrorKind.DatabaseUnreadable);
        }
        if (anyUsageTable && !anyDataQuerySucceeded)
        {
            throw new OpenCodeUsageError(OpenCodeUsageErrorKind.DatabaseUnreadable);
        }

        // Empty rows stay in the dedup set so a newer V2 row can suppress an older copy of the same
        // message, but a selected row with no cost and no tokens must not create a zero-usage day.
        var rows = rowsById.Values
            .Select(candidate => candidate.Row)
            .Concat(anonymousRows)
            .Where(row => row.Cost is not null || row.Tokens > 0)
            .OrderBy(r => r.Ms)
            .ThenBy(r => r.Id ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var tileSince = OpenCodeLocalCalendar.Midnight(today.AddDays(-30), _timeZone);
        var accumulator = new DailyUsageAccumulator();
        var accumulatorOverflowed = false;
        var hasMissingCost = false;
        foreach (var row in rows)
        {
            DateTimeOffset date;
            try
            {
                date = DateTimeOffset.FromUnixTimeMilliseconds((long)row.Ms);
            }
            catch
            {
                continue;
            }
            if (date < tileSince) continue;
            // Track incomplete cost only for the selected (deduplicated) rows that actually render in
            // the 30-day spend window; suppressed or out-of-window rows must not qualify a subtotal.
            if (row.Cost is null) hasMissingCost = true;
            var localDate = TimeZoneInfo.ConvertTime(date, _timeZone);
            try
            {
                accumulator.Add(DailyUsageAccumulator.DayKey(localDate), row.Tokens, row.Cost, row.Model);
            }
            catch (OverflowException)
            {
                accumulatorOverflowed = true;
                AppLog.Warn(LogTag.Plugin("opencode"), "local usage token sum exceeded Int64 range");
            }
        }
        if (accumulatorOverflowed) failed++;

        var hasHostedUsage = rows.Count > 0;
        var hasGoUsage = rows.Any(r => r.ProviderId == GoProviderId);
        var version = await ReadVersionHintAsync(cancellationToken).ConfigureAwait(false);
        LogLayout(paths.Count, readable, failed, unsupported, hasLegacySchema, hasV2Schema, version);

        return new OpenCodeUsageScan(
            accumulator.Build(),
            new OpenCodeScanReport(
                paths.Count,
                readable,
                failed,
                unsupported,
                hasLegacySchema,
                hasV2Schema,
                hasHostedUsage,
                hasGoUsage,
                version,
                hasMissingCost),
            calendarNow);

        void AddRow(OpenCodeUsageRow row, string sourcePath)
        {
            if (row.Id is null)
            {
                anonymousRows.Add(row);
                return;
            }
            if (!rowsById.TryGetValue(row.Id, out var existing)
                || IsPreferred(row, sourcePath, existing))
            {
                rowsById[row.Id] = new Candidate(row, sourcePath);
            }
        }
    }

    private sealed record Candidate(OpenCodeUsageRow Row, string SourcePath);

    private static bool IsPreferred(
        OpenCodeUsageRow candidate,
        string candidatePath,
        Candidate current)
    {
        // Generation first: migration copies the same ID to V2, so V2 always wins even when its
        // timestamp is older. Within one generation prefer the newest update, and break exact ties
        // by ordinal-greater source path so the result never depends on enumeration order.
        if (candidate.IsV2 != current.Row.IsV2) return candidate.IsV2;
        if (candidate.EffectiveMs != current.Row.EffectiveMs)
        {
            return candidate.EffectiveMs > current.Row.EffectiveMs;
        }
        return string.CompareOrdinal(candidatePath, current.SourcePath) > 0;
    }

    public bool HasHostedUsage()
    {
        List<string> paths;
        try
        {
            paths = _databasePaths();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Plugin("opencode"), $"usage probe: data directory unreadable: {ex.Message}");
            return true;
        }

        foreach (var path in paths)
        {
            try
            {
                var tables = OpenCodeDatabaseSchema.Tables(_sqlite.QueryValue(path, OpenCodeDatabaseSchema.TableNamesSql));
                if (tables.Contains("message")
                    && _sqlite.QueryValue(path, OpenCodeUsageQuery.LegacyProbeSql) is not null) return true;
                if (tables.Contains("session_message"))
                {
                    HashSet<string>? columns = null;
                    try
                    {
                        columns = OpenCodeDatabaseSchema.Tables(
                            _sqlite.QueryValue(path, OpenCodeDatabaseSchema.ColumnNamesSql("session_message")));
                    }
                    catch (Exception ex)
                    {
                        // Assume the documented V2 shape, but leave a diagnostic for the downgrade.
                        AppLog.Warn(LogTag.Plugin("opencode"),
                            $"V2 column probe failed during usage probe for {path}; assuming the type column exists: {ex.Message}");
                    }
                    var probe = columns is not null && !columns.Contains("type")
                        ? OpenCodeUsageQuery.V2ProbeNoTypeSql
                        : OpenCodeUsageQuery.V2ProbeSql;
                    if (_sqlite.QueryValue(path, probe) is not null) return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogTag.Plugin("opencode"), $"usage probe failed for {path}: {ex.Message}");
            }
        }
        return false;
    }

    private async Task<string?> ReadVersionHintAsync(CancellationToken cancellationToken)
    {
        if (_versionProbe is null) return null;
        try
        {
            var hint = await _versionProbe.GetAsync(cancellationToken).ConfigureAwait(false);
            return hint.Version;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug(LogTag.Plugin("opencode"), $"version hint unavailable: {ex.Message}");
            return null;
        }
    }

    private void LogLayout(
        int databases,
        int readable,
        int failed,
        int unsupported,
        bool legacy,
        bool v2,
        string? version)
    {
        var layout = $"dbs={databases} readable={readable} failed={failed} unsupported={unsupported} legacy={legacy} v2={v2} version={version ?? "unknown"}";
        if (layout == _lastLayoutLog) return;
        _lastLayoutLog = layout;
        AppLog.Info(LogTag.Plugin("opencode"), $"local stores: {layout}");
    }

}
