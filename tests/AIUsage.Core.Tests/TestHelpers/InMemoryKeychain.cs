using AIUsage.Core.Services;

namespace AIUsage.Core.Tests.TestHelpers;

/// <summary>In-memory IKeychainAccessing for tests, avoiding any touch of the real Windows
/// Credential Manager.</summary>
public sealed class InMemoryKeychain : IKeychainAccessing
{
    private readonly Dictionary<string, string> _values = new();

    public InMemoryKeychain Set(string service, string value)
    {
        _values[service] = value;
        return this;
    }

    public string? ReadGenericPassword(string service) => _values.GetValueOrDefault(service);
    public string? ReadGenericPassword(string service, string account) => _values.GetValueOrDefault($"{service}:{account}");
    public void WriteGenericPassword(string service, string value) => _values[service] = value;
    public bool? GenericPasswordExists(string service) => _values.ContainsKey(service);
}
