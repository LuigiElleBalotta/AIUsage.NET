using AIUsage.Core.Services;

namespace AIUsage.Core.Tests.TestHelpers;

/// <summary>In-memory ITextFileAccessing for tests, avoiding any touch of the real filesystem. Paths
/// are used as-is (no "~" expansion) — tests should pass fully resolved paths.</summary>
public sealed class InMemoryFileSystem : ITextFileAccessing
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryFileSystem Write(string path, string text)
    {
        _files[Normalize(path)] = text;
        return this;
    }

    public bool Exists(string path) => _files.ContainsKey(Normalize(path));
    public string? ReadTextIfPresent(string path) => _files.GetValueOrDefault(Normalize(path));
    public string ReadText(string path) => _files.TryGetValue(Normalize(path), out var text) ? text : throw new FileNotFoundException(path);
    public void WriteText(string path, string text) => _files[Normalize(path)] = text;
    public void Remove(string path) => _files.Remove(Normalize(path));

    private static string Normalize(string path) => path.Replace('\\', '/');
}
