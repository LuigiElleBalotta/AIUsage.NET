using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

/// <summary>
/// Where OpenCode keeps its local data. Resolution mirrors OpenCode itself: an explicit
/// OPENCODE_DATA_DIR wins, then XDG_DATA_HOME/opencode, then the canonical home data directory
/// (~/.local/share/opencode — what OpenCode 2.x reports on Windows too). The older
/// %LOCALAPPDATA%\opencode location is still honoured when the canonical directory is absent but
/// that legacy directory exists, so existing installs are not lost; the two are never merged.
/// </summary>
public static class OpenCodePaths
{
    public static string DataDirectory(
        IEnvironmentReading environment,
        string homeDirectory,
        Func<string, bool>? directoryExists = null,
        Func<string>? localAppDataDirectory = null,
        Func<string, string>? expandHome = null)
    {
        var expand = expandHome ?? PathHelpers.ExpandHome;
        var exists = directoryExists ?? Directory.Exists;

        var overrideDir = environment.Value("OPENCODE_DATA_DIR")?.Trim().NilIfEmpty();
        if (overrideDir is not null) return expand(overrideDir).TrimmingTrailingSlashes();

        var xdg = environment.Value("XDG_DATA_HOME")?.Trim().NilIfEmpty();
        if (xdg is not null) return Path.Combine(expand(xdg).TrimmingTrailingSlashes(), "opencode");

        var canonical = Path.Combine(homeDirectory, ".local", "share", "opencode");
        if (exists(canonical)) return canonical;

        var localAppData = localAppDataDirectory
            ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var legacy = Path.Combine(localAppData(), "opencode");
        return exists(legacy) ? legacy : canonical;
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
