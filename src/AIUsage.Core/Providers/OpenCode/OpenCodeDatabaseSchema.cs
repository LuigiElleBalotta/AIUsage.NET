namespace AIUsage.Core.Providers.OpenCode;

/// <summary>
/// Small SQLite schema probes shared by the usage scanner and the credential store. Readers are
/// chosen from the tables that actually exist instead of assuming one OpenCode release's layout.
/// </summary>
internal static class OpenCodeDatabaseSchema
{
    public const string TableNamesSql =
        "SELECT group_concat(name, ',') FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";

    public static HashSet<string> Tables(string? commaSeparated)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(commaSeparated)) return tables;
        foreach (var name in commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            tables.Add(name);
        }
        return tables;
    }

    public static bool HasAnyUsageTable(HashSet<string> tables) =>
        tables.Contains("message") || tables.Contains("session_message");

    /// <summary>
    /// A DB with no usage table is only suspicious when it also has none of the well-known OpenCode
    /// tables. An empty DB, or a V2 DB with credentials but no messages yet, is valid and yields zero.
    /// </summary>
    public static bool LooksLikeKnownSchema(HashSet<string> tables) =>
        tables.Count == 0
        || tables.Contains("message")
        || tables.Contains("session_message")
        || tables.Contains("session")
        || tables.Contains("credential")
        || tables.Contains("part")
        || tables.Contains("project");

    public static string ColumnNamesSql(string table) =>
        $"SELECT group_concat(name, ',') FROM pragma_table_info('{table.Replace("'", "''", StringComparison.Ordinal)}')";
}
