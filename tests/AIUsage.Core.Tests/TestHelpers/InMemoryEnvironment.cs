using AIUsage.Core.Services;

namespace AIUsage.Core.Tests.TestHelpers;

/// <summary>Scripted IEnvironmentReading for tests, avoiding any touch of real process/user env vars.</summary>
public sealed class InMemoryEnvironment : IEnvironmentReading
{
    private readonly Dictionary<string, string> _values = new();

    public InMemoryEnvironment Set(string name, string? value)
    {
        if (value is null) _values.Remove(name);
        else _values[name] = value;
        return this;
    }

    public string? Value(string name) => _values.GetValueOrDefault(name);
}
