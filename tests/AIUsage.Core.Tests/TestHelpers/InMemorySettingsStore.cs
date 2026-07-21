using AIUsage.Core.Services;

namespace AIUsage.Core.Tests.TestHelpers;

/// <summary>In-memory ISettingsStore for tests, avoiding any touch of the real
/// %LOCALAPPDATA%\AIUsage\settings.json file.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _strings = new();
    private readonly Dictionary<string, bool> _bools = new();

    public string? GetString(string key) => _strings.GetValueOrDefault(key);
    public void SetString(string key, string value) => _strings[key] = value;
    public bool? GetBool(string key) => _bools.TryGetValue(key, out var v) ? v : null;
    public void SetBool(string key, bool value) => _bools[key] = value;
    public void Remove(string key)
    {
        _strings.Remove(key);
        _bools.Remove(key);
    }
}
