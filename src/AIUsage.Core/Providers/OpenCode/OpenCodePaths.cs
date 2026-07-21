using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

/// <summary>
/// Where OpenCode keeps its local data. Windows resolution mirrors OpenCode itself: an explicit
/// OPENCODE_DATA_DIR wins, then XDG_DATA_HOME/opencode, then the Windows default (%LOCALAPPDATA%\opencode,
/// the direct counterpart of ~/.local/share/opencode).
/// </summary>
public static class OpenCodePaths
{
    public static string DataDirectory(IEnvironmentReading environment, string homeDirectory)
    {
        var overrideDir = environment.Value("OPENCODE_DATA_DIR")?.Trim().NilIfEmpty();
        if (overrideDir is not null) return PathHelpers.ExpandHome(overrideDir).TrimmingTrailingSlashes();

        var xdg = environment.Value("XDG_DATA_HOME")?.Trim().NilIfEmpty();
        if (xdg is not null) return Path.Combine(PathHelpers.ExpandHome(xdg).TrimmingTrailingSlashes(), "opencode");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opencode");
    }

    public static string AuthFilePath(string dataDirectory) => Path.Combine(dataDirectory.TrimmingTrailingSlashes(), "auth.json");

    public static List<string> DatabaseFiles(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory)) return new List<string>();
        return Directory.EnumerateFiles(dataDirectory)
            .Where(f => Path.GetFileName(f).StartsWith("opencode", StringComparison.Ordinal) && f.EndsWith(".db", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }
}
