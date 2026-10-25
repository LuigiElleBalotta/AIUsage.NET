using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AIUsage.Core.Tests.Providers;

/// <summary>
/// A temporary OpenCode data directory with real SQLite databases. Tests only ever read these files
/// through the production scanner/credential store; nothing touches the current user's real data.
/// </summary>
internal sealed class OpenCodeTestStore : IDisposable
{
    public OpenCodeTestStore()
    {
        Root = Path.Combine(Path.GetTempPath(), "AIUsageOpenCodeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public OpenCodeTestDatabase Database(string fileName = "opencode.db") =>
        new(Path.Combine(Root, fileName));

    public void WriteAuthJson(string json) => File.WriteAllText(Path.Combine(Root, "auth.json"), json);

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}

internal sealed class OpenCodeTestDatabase
{
    public OpenCodeTestDatabase(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public void CreateLegacySchema() => Execute("""
        CREATE TABLE IF NOT EXISTS message (
            id TEXT PRIMARY KEY,
            session_id TEXT,
            time_created INTEGER,
            time_updated INTEGER,
            data TEXT
        );
        """);

    public void CreateV2Schema() => Execute("""
        CREATE TABLE IF NOT EXISTS session_message (
            id TEXT PRIMARY KEY,
            session_id TEXT,
            type TEXT,
            seq INTEGER,
            time_created INTEGER,
            time_updated INTEGER,
            data TEXT
        );
        """);

    public void CreateCredentialSchema() => Execute("""
        CREATE TABLE IF NOT EXISTS credential (
            id TEXT PRIMARY KEY,
            integration_id TEXT,
            label TEXT,
            value TEXT,
            method_id TEXT,
            active INTEGER,
            time_created INTEGER,
            time_updated INTEGER
        );
        """);

    public void CreateUnrelatedSchema() => Execute("CREATE TABLE IF NOT EXISTS something_else (id TEXT PRIMARY KEY, value TEXT);");

    public void InsertLegacy(
        string id,
        long timeMs,
        string providerId = "opencode-go",
        string modelId = "claude-sonnet-4",
        double? cost = 0.5,
        long? totalTokens = 100,
        string role = "assistant")
    {
        var data = new Dictionary<string, object?>
        {
            ["role"] = role,
            ["providerID"] = providerId,
            ["modelID"] = modelId
        };
        if (cost is { } costValue) data["cost"] = costValue;
        if (totalTokens is { } tokenValue) data["tokens"] = new Dictionary<string, object?> { ["total"] = tokenValue };
        InsertLegacyRaw(id, timeMs, JsonSerializer.Serialize(data));
    }

    public void InsertLegacyRaw(string id, long timeMs, string dataJson) =>
        Execute(
            "INSERT OR REPLACE INTO message (id, session_id, time_created, time_updated, data) VALUES (@id, @session, @time, @time, @data);",
            ("@id", id), ("@session", "session-" + id), ("@time", timeMs), ("@data", dataJson));

    public void InsertV2(
        string id,
        long timeMs,
        string providerId = "opencode-go",
        string modelId = "claude-sonnet-4",
        double? cost = 0.5,
        long input = 0,
        long output = 0,
        long reasoning = 0,
        long cacheRead = 0,
        long cacheWrite = 0,
        string type = "assistant")
    {
        var data = new Dictionary<string, object?>
        {
            ["model"] = new Dictionary<string, object?> { ["providerID"] = providerId, ["id"] = modelId },
            ["tokens"] = new Dictionary<string, object?>
            {
                ["input"] = input,
                ["output"] = output,
                ["reasoning"] = reasoning,
                ["cache"] = new Dictionary<string, object?> { ["read"] = cacheRead, ["write"] = cacheWrite }
            }
        };
        if (cost is { } costValue) data["cost"] = costValue;
        InsertV2Raw(id, timeMs, type, JsonSerializer.Serialize(data));
    }

    public void InsertV2Raw(string id, long timeMs, string type, string dataJson) =>
        Execute(
            "INSERT OR REPLACE INTO session_message (id, session_id, type, seq, time_created, time_updated, data) VALUES (@id, @session, @type, 0, @time, @time, @data);",
            ("@id", id), ("@session", "session-" + id), ("@type", type), ("@time", timeMs), ("@data", dataJson));

    public void InsertCredential(
        string id,
        string integrationId,
        string valueJson,
        long timeUpdated,
        int active = 1,
        string label = "test") =>
        Execute(
            "INSERT OR REPLACE INTO credential (id, integration_id, label, value, method_id, active, time_created, time_updated) VALUES (@id, @integration, @label, @value, 'device', @active, @time, @time);",
            ("@id", id), ("@integration", integrationId), ("@label", label), ("@value", valueJson),
            ("@active", active), ("@time", timeUpdated));

    public void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }
}
