using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

public sealed record OpenCodeUsageScan(LogUsageScan LogScan, OpenCodeGoWindows? GoWindows);

/// <summary>
/// Reads OpenCode's local SQLite logs (%LOCALAPPDATA%\opencode\opencode*.db) and builds the usage the
/// provider renders. Direct port of OpenCodeUsageScanner (minus the persistent read-failure edge-trigger
/// reporter — simplified to plain per-call logging, since the in-memory scan cache already avoids
/// re-querying unnecessarily every refresh cycle here is cheap SQLite reads, not JSONL re-parses).
/// </summary>
public sealed class OpenCodeUsageScanner
{
    public static readonly string[] HostedProviderIds = { "opencode-go", "opencode" };
    public const string GoProviderId = "opencode-go";

    private readonly ISqliteAccessing _sqlite;
    private readonly Func<List<string>> _databasePaths;

    public OpenCodeUsageScanner(ISqliteAccessing? sqlite = null, Func<List<string>>? databasePaths = null)
    {
        _sqlite = sqlite ?? new SqliteDataAccessor();
        _databasePaths = databasePaths ?? DefaultDatabasePaths;
    }

    private static List<string> DefaultDatabasePaths()
    {
        var dir = OpenCodePaths.DataDirectory(new ProcessEnvironmentReader(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return OpenCodePaths.DatabaseFiles(dir);
    }

    public async Task<OpenCodeUsageScan?> ScanAsync(DateTimeOffset now, int daysBack = 33, bool hasGoKey = false, CancellationToken cancellationToken = default)
    {
        List<string> paths;
        try
        {
            paths = await Task.Run(_databasePaths, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Plugin("opencode"), $"data directory unreadable: {ex.Message}");
            throw new OpenCodeUsageError(OpenCodeUsageErrorKind.DatabaseUnreadable);
        }
        if (paths.Count == 0) return null;

        var cutoffMs = (long)((now.ToUnixTimeMilliseconds() / 1000.0) - daysBack * 86_400) * 1000;
        var rows = new List<Row>();
        double? anchorMs = null;
        var failures = 0;

        foreach (var path in paths)
        {
            try
            {
                var json = await Task.Run(() => _sqlite.QueryValue(path, DataSql(cutoffMs)), cancellationToken).ConfigureAwait(false);
                if (json is not null) rows.AddRange(ParseRows(json));
            }
            catch (Exception ex)
            {
                failures++;
                AppLog.Warn(LogTag.Plugin("opencode"), $"usage query failed for {path}: {ex.Message}");
                continue;
            }
            try
            {
                var anchorText = await Task.Run(() => _sqlite.QueryValue(path, AnchorSql), cancellationToken).ConfigureAwait(false);
                if (anchorText is not null && double.TryParse(anchorText.Trim(), out var value))
                {
                    anchorMs = anchorMs is { } a ? Math.Min(a, value) : value;
                }
            }
            catch { /* best-effort anchor; falls back to calendar month */ }
        }
        if (failures == paths.Count) throw new OpenCodeUsageError(OpenCodeUsageErrorKind.DatabaseUnreadable);

        var tileSince = JsonlScanning.SinceDate(30, now);
        var accumulator = new DailyUsageAccumulator();
        foreach (var row in rows)
        {
            var date = DateTimeOffset.FromUnixTimeMilliseconds((long)row.Ms);
            if (date < tileSince) continue;
            accumulator.Add(DailyUsageAccumulator.DayKey(date), row.Tokens, row.Cost, row.Model);
        }
        var logScan = accumulator.Build();

        var goCosts = rows.Where(r => r.ProviderId == GoProviderId).Select(r => (r.Ms, r.Cost)).ToList();
        OpenCodeGoWindows? goWindows = (hasGoKey || goCosts.Count > 0)
            ? OpenCodeGoWindowMath.Compute(goCosts, anchorMs, now)
            : null;

        return new OpenCodeUsageScan(logScan, goWindows);
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
                var value = _sqlite.QueryValue(path, ProbeSql);
                if (!string.IsNullOrEmpty(value)) return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogTag.Plugin("opencode"), $"usage probe failed for {path}: {ex.Message}");
            }
        }
        return false;
    }

    private sealed record Row(double Ms, double Cost, int Tokens, string Model, string ProviderId);

    private static List<Row> ParseRows(string json)
    {
        var rows = new List<Row>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 5) continue;
                var arr = entry.EnumerateArray().ToList();
                if (ProviderParse.Number(arr[0]) is not { } ms) continue;
                if (ProviderParse.Number(arr[1]) is not { } cost || cost < 0) continue;
                if (arr[4].ValueKind != JsonValueKind.String) continue;
                var providerId = arr[4].GetString()!;
                var tokens = (int)Math.Min(Math.Max(ProviderParse.Number(arr[2]) ?? 0, 0), 1e15);
                var model = arr[3].ValueKind == JsonValueKind.String ? arr[3].GetString()! : "";
                rows.Add(new Row(ms, cost, tokens, model, providerId));
            }
        }
        catch
        {
            // malformed JSON payload; return what was parsed so far (none)
        }
        return rows;
    }

    private static readonly string ProviderFilter = "(" + string.Join(",", HostedProviderIds.Select(p => $"'{p}'")) + ")";

    private static string DataSql(long cutoffMs) => $"""
        SELECT json_group_array(json_array(
                 time_created,
                 json_extract(data,'$.cost'),
                 COALESCE(json_extract(data,'$.tokens.total'),0),
                 json_extract(data,'$.modelID'),
                 json_extract(data,'$.providerID')))
        FROM message
        WHERE time_created >= {cutoffMs}
          AND json_valid(data)
          AND json_extract(data,'$.role') = 'assistant'
          AND json_extract(data,'$.providerID') IN {ProviderFilter}
          AND json_type(data,'$.cost') IN ('integer','real');
        """;

    private static readonly string AnchorSql = $"""
        SELECT MIN(time_created) FROM message
        WHERE json_valid(data)
          AND json_extract(data,'$.role') = 'assistant'
          AND json_extract(data,'$.providerID') = '{GoProviderId}'
          AND json_type(data,'$.cost') IN ('integer','real');
        """;

    private static readonly string ProbeSql = $"""
        SELECT 1 FROM message
        WHERE json_valid(data)
          AND json_extract(data,'$.role') = 'assistant'
          AND json_extract(data,'$.providerID') IN {ProviderFilter}
          AND json_type(data,'$.cost') IN ('integer','real')
        LIMIT 1;
        """;
}
