using System.Text.Json;

namespace AIUsage.Core.Services;

/// <summary>
/// Simple key-value settings persistence — the Windows counterpart of macOS's UserDefaults.standard.
/// Backed by a single JSON file under %LOCALAPPDATA%\AIUsage\settings.json. Not a registry-based
/// implementation by design: a plain file is easier to inspect/back up and avoids HKCU write
/// permission edge cases in sandboxed contexts.
/// </summary>
public interface ISettingsStore
{
    string? GetString(string key);
    void SetString(string key, string value);
    bool? GetBool(string key);
    void SetBool(string key, bool value);
    void Remove(string key);
}

public sealed class FileSettingsStore : ISettingsStore
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsage", "settings.json");

    public static readonly FileSettingsStore Shared = new();

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, JsonElement>? _cache;

    public FileSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    private Dictionary<string, JsonElement> Load()
    {
        lock (_lock)
        {
            if (_cache is not null) return _cache;
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
                }
                else
                {
                    _cache = new();
                }
            }
            catch
            {
                _cache = new();
            }
            return _cache;
        }
    }

    private void Save(Dictionary<string, JsonElement> data)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(data));
                _cache = data;
            }
            catch
            {
                // best effort; settings persistence must never crash the app
            }
        }
    }

    public string? GetString(string key)
    {
        var data = Load();
        return data.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public void SetString(string key, string value)
    {
        var data = new Dictionary<string, JsonElement>(Load());
        data[key] = JsonSerializer.SerializeToElement(value);
        Save(data);
    }

    public bool? GetBool(string key)
    {
        var data = Load();
        if (!data.TryGetValue(key, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    public void SetBool(string key, bool value)
    {
        var data = new Dictionary<string, JsonElement>(Load());
        data[key] = JsonSerializer.SerializeToElement(value);
        Save(data);
    }

    public void Remove(string key)
    {
        var data = new Dictionary<string, JsonElement>(Load());
        if (data.Remove(key)) Save(data);
    }
}
