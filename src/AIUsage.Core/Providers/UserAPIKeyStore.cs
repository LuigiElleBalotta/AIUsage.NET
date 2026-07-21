using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers;

/// <summary>
/// A user-supplied API key already on the machine (env var or a small JSON/plain-text config file).
/// Direct port of the Swift UserAPIKeyStore.
/// </summary>
public sealed class UserAPIKeyStore
{
    public enum Failure
    {
        MissingKey,
        SaveFailed,
        DeleteFailed
    }

    public IReadOnlyList<string> ConfigPaths { get; }
    public IReadOnlyList<string> EnvironmentNames { get; }
    private readonly ITextFileAccessing _files;
    private readonly IEnvironmentReading _environment;
    private readonly Func<Failure, Exception> _makeError;

    public UserAPIKeyStore(
        IReadOnlyList<string> configPaths,
        IReadOnlyList<string> environmentNames,
        ITextFileAccessing files,
        IEnvironmentReading environment,
        Func<Failure, Exception> makeError)
    {
        ConfigPaths = configPaths;
        EnvironmentNames = environmentNames;
        _files = files;
        _environment = environment;
        _makeError = makeError;
    }

    public string? LoadKey() => KeyFromConfigFile() ?? KeyFromEnvironment();

    public APIKeyStatus KeyStatus()
    {
        var hasConfig = KeyFromConfigFile() is not null;
        var hasEnv = KeyFromEnvironment() is not null;
        return (hasConfig, hasEnv) switch
        {
            (true, true) => APIKeyStatus.OverrideActive,
            (true, false) => APIKeyStatus.Saved,
            (false, true) => APIKeyStatus.FromEnvironment,
            _ => APIKeyStatus.NotSet
        };
    }

    public void SaveKey(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0) throw _makeError(Failure.MissingKey);
        var text = JsonSerializer.Serialize(new Dictionary<string, string> { ["apiKey"] = trimmed });
        try
        {
            _files.WriteText(ConfigPaths[0], text);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogTag.Auth, $"save API key to {ConfigPaths[0]} failed: {ex.Message}");
            throw _makeError(Failure.SaveFailed);
        }
    }

    public void DeleteKey()
    {
        foreach (var path in ConfigPaths)
        {
            if (!_files.Exists(path)) continue;
            try
            {
                _files.Remove(path);
            }
            catch (Exception ex)
            {
                AppLog.Error(LogTag.Auth, $"delete API key at {path} failed: {ex.Message}");
                throw _makeError(Failure.DeleteFailed);
            }
        }
    }

    private string? KeyFromEnvironment()
    {
        foreach (var name in EnvironmentNames)
        {
            var value = _environment.Value(name)?.Trim();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    private string? KeyFromConfigFile()
    {
        foreach (var path in ConfigPaths)
        {
            if (!_files.Exists(path)) continue;
            string? text;
            try { text = _files.ReadText(path); } catch { continue; }
            var key = KeyFromConfigText(text);
            if (key is not null) return key;
        }
        return null;
    }

    public static string? KeyFromConfigText(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in new[] { "apiKey", "api_key", "key" })
                {
                    if (doc.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        var s = value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                return null;
            }
        }
        catch (JsonException)
        {
            // not JSON — fall through to plain-text handling
        }

        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.Contains('{') ? null : trimmed;
    }
}
