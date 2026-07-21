using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsage.Core.Models;
using AIUsage.Core.Support;

namespace AIUsage.Core.Stores;

/// <summary>
/// Local, single-file backup/restore for per-provider usage history — the only thing the Swift
/// edition's iCloud sync actually moved (see PORTING_NOTES.md; iCloud syncs only
/// <see cref="ProviderUsageHistory"/>, never settings/layout/pins). Rather than a live multi-device
/// sync (no Windows-native equivalent of iCloud Drive's app container + NSMetadataQuery live-update
/// plumbing was worth building), this is an explicit user action: "Export Usage History..." writes one
/// JSON file the user can back up or copy to another machine; "Import Usage History..." reads one back
/// and merges it into the current cache via <see cref="ProviderSnapshotCache.MergeUsageHistory"/>.
/// </summary>
public sealed record UsageHistoryBackupDocument(
    string Schema,
    string DeviceName,
    DateTimeOffset ExportedAt,
    Dictionary<string, ProviderUsageHistory> Providers
);

public enum UsageHistoryBackupError
{
    NoHistoryToExport,
    UnsupportedSchema,
    InvalidFile
}

public sealed class UsageHistoryBackupException : Exception
{
    public UsageHistoryBackupError Kind { get; }

    public UsageHistoryBackupException(UsageHistoryBackupError kind) : base(Describe(kind))
    {
        Kind = kind;
    }

    private static string Describe(UsageHistoryBackupError kind) => kind switch
    {
        UsageHistoryBackupError.NoHistoryToExport => "No usage history to export yet — refresh at least one provider first.",
        UsageHistoryBackupError.UnsupportedSchema => "This backup file was written by a newer AIUsage.NET version. Update AIUsage.NET.",
        UsageHistoryBackupError.InvalidFile => "This file isn't a valid AIUsage.NET usage-history backup.",
        _ => "Usage history backup error."
    };
}

public static class UsageHistoryBackupStore
{
    public const string Schema = "aiusage.history.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Builds the exportable document from every provider's currently cached usage history,
    /// then writes it to <paramref name="filePath"/>. Throws <see cref="UsageHistoryBackupException"/>
    /// with <see cref="UsageHistoryBackupError.NoHistoryToExport"/> if no provider has any history yet.</summary>
    public static void Export(ProviderSnapshotCache cache, string filePath)
    {
        var providers = new Dictionary<string, ProviderUsageHistory>();
        foreach (var (providerId, snapshot) in cache.AllSnapshots())
        {
            if (snapshot.UsageHistory is { } history) providers[providerId] = history;
        }
        if (providers.Count == 0) throw new UsageHistoryBackupException(UsageHistoryBackupError.NoHistoryToExport);

        var document = new UsageHistoryBackupDocument(Schema, Environment.MachineName, DateTimeOffset.UtcNow, providers);
        var json = JsonSerializer.Serialize(document, JsonOptions);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, json);
        AppLog.Info(LogTag.Config, $"usage history exported to {filePath} ({providers.Count} provider(s))");
    }

    /// <summary>Reads a backup file and merges every provider's history into the current cache.
    /// Returns the number of providers actually merged (a provider with no current cached snapshot is
    /// skipped — see <see cref="ProviderSnapshotCache.MergeUsageHistory"/>). Throws
    /// <see cref="UsageHistoryBackupException"/> on an unreadable or newer-schema file.</summary>
    public static int Import(ProviderSnapshotCache cache, string filePath)
    {
        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Config, $"usage history import failed to read {filePath}: {ex.Message}");
            throw new UsageHistoryBackupException(UsageHistoryBackupError.InvalidFile);
        }

        UsageHistoryBackupDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<UsageHistoryBackupDocument>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Config, $"usage history import failed to parse {filePath}: {ex.Message}");
            throw new UsageHistoryBackupException(UsageHistoryBackupError.InvalidFile);
        }
        if (document is null) throw new UsageHistoryBackupException(UsageHistoryBackupError.InvalidFile);
        if (document.Schema != Schema) throw new UsageHistoryBackupException(UsageHistoryBackupError.UnsupportedSchema);

        var mergedCount = 0;
        foreach (var (providerId, history) in document.Providers)
        {
            if (cache.MergeUsageHistory(providerId, history)) mergedCount++;
        }
        AppLog.Info(LogTag.Config, $"usage history imported from {filePath} ({mergedCount}/{document.Providers.Count} provider(s) merged)");
        return mergedCount;
    }
}
