using System.Reflection;

namespace AIUsage.Tray;

/// <summary>Reads the app version stamped by Directory.Build.props (or -p:Version at publish time),
/// so the tray window, Settings window, and this project's csproj-level version all agree.</summary>
internal static class AppVersion
{
    public static string Display()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip a source-control metadata suffix (e.g. "0.1.0+abcdef1234") that dotnet publish
            // appends by default when it can read a git commit hash — not useful to show a user.
            var plusIndex = informational.IndexOf('+');
            return plusIndex >= 0 ? informational[..plusIndex] : informational;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "dev" : version.ToString(3);
    }
}
