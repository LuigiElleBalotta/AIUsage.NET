using System.Reflection;

namespace AIUsage.Cli;

/// <summary>Reads the app version stamped by Directory.Build.props (or -p:Version at publish time),
/// matching AIUsage.Tray/AppVersion.cs so `aiusage --version` and the tray window always agree.</summary>
internal static class CliVersion
{
    public static string Display()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            return plusIndex >= 0 ? informational[..plusIndex] : informational;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "dev" : version.ToString(3);
    }
}
