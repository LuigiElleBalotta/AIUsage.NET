using System.Diagnostics;
using AIUsage.Core.Support;

namespace AIUsage.Core.Services;

/// <summary>
/// Finds a running Codeium-derived language server (Antigravity's bundled language_server, or the
/// agy CLI) and returns the CSRF token + listening ports needed to call its local Connect-RPC service.
///
/// Windows port note: the macOS edition shells out to `ps` (process + command line) and `lsof`
/// (listening ports per PID). Windows has no direct equivalent of either in a single command, so this
/// uses WMI (Win32_Process.CommandLine, via System.Management) for process discovery and `netstat -ano`
/// for the PID-to-listening-port mapping — the standard Windows tools for each job. The pure matching
/// logic (rankedCandidates, extractFlag, markerRank, commandMatchesProcess) is a direct, unit-testable
/// port of the Swift implementation.
/// </summary>
public sealed class LanguageServerDiscovery
{
    public sealed record Options(string ProcessName, IReadOnlyList<string> Markers, string CsrfFlag, string? PortFlag);

    public sealed record Result(int Pid, string Csrf, IReadOnlyList<int> Ports, int? ExtensionPort);

    public Result? Discover(Options options)
    {
        List<(int Pid, string Command)> processes;
        try
        {
            processes = QueryProcessesWmi();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Subprocess, $"ls discover: process query failed for {options.ProcessName}: {ex.Message}");
            return null;
        }

        var candidates = RankedCandidates(processes, options);
        if (candidates.Count == 0)
        {
            AppLog.Info(LogTag.Subprocess, $"ls discover: {options.ProcessName} process not found");
            return null;
        }

        foreach (var candidate in candidates)
        {
            string csrf;
            if (string.IsNullOrWhiteSpace(options.CsrfFlag))
            {
                csrf = "";
            }
            else if (ExtractFlag(candidate.Command, options.CsrfFlag) is { } value)
            {
                csrf = value;
            }
            else
            {
                continue;
            }

            int? extensionPort = options.PortFlag is { } pf && ExtractFlag(candidate.Command, pf) is { } portStr && int.TryParse(portStr, out var p)
                ? p
                : null;

            var ports = ListeningPortsForPid(candidate.Pid);
            if (ports.Count == 0 && extensionPort is null) continue;

            AppLog.Info(LogTag.Subprocess, $"ls discover: found {options.ProcessName} pid={candidate.Pid} ports=[{string.Join(",", ports)}]");
            return new Result(candidate.Pid, csrf, ports, extensionPort);
        }
        return null;
    }

    private static List<(int Pid, string Command)> QueryProcessesWmi()
    {
        var results = new List<(int Pid, string Command)>();
        using var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
        foreach (System.Management.ManagementObject obj in searcher.Get())
        {
            var pid = Convert.ToInt32(obj["ProcessId"]);
            var commandLine = obj["CommandLine"] as string;
            if (!string.IsNullOrEmpty(commandLine))
            {
                results.Add((pid, commandLine));
            }
        }
        return results;
    }

    private static List<int> ListeningPortsForPid(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p TCP")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return new List<int>();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return ParseListeningPorts(output, pid);
        }
        catch
        {
            return new List<int>();
        }
    }

    /// <summary>Parse `netstat -ano -p TCP` output for LISTENING lines owned by `pid`. Format:
    /// "  TCP    127.0.0.1:52168        0.0.0.0:0              LISTENING       1234"</summary>
    public static List<int> ParseListeningPorts(string netstatOutput, int pid)
    {
        var ports = new HashSet<int>();
        foreach (var rawLine in netstatOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.Contains("LISTENING", StringComparison.Ordinal)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[^1], out var ownerPid) || ownerPid != pid) continue;
            var localAddress = parts[1];
            var colon = localAddress.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(localAddress[(colon + 1)..], out var port)) continue;
            if (port > 0 && port < 65536) ports.Add(port);
        }
        return ports.OrderBy(p => p).ToList();
    }

    // MARK: - Pure helpers (direct port of the Swift matching logic)

    public static List<(int Pid, string Command)> RankedCandidates(List<(int Pid, string Command)> processes, Options options)
    {
        var processNameLower = options.ProcessName.ToLowerInvariant();
        var markersLower = options.Markers.Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 0).ToList();

        var ranked = new List<(int Rank, int Pid, string Command)>();
        foreach (var (pid, command) in processes)
        {
            if (!CommandMatchesProcess(command, processNameLower)) continue;
            var rank = MarkerRank(command, markersLower);
            if (rank is null) continue;
            ranked.Add((rank.Value, pid, command));
        }
        return ranked.OrderBy(r => r.Rank).Select(r => (r.Pid, r.Command)).ToList();
    }

    public static string? ExtractFlag(string command, string flag)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var flagEq = flag + "=";
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == flag)
            {
                if (i + 1 < parts.Length) return parts[i + 1];
            }
            else if (parts[i].StartsWith(flagEq, StringComparison.Ordinal))
            {
                return parts[i][flagEq.Length..];
            }
        }
        return null;
    }

    public static int? MarkerRank(string command, List<string> markersLower)
    {
        if (markersLower.Count == 0) return 0;

        var ideName = ExtractFlag(command, "--ide_name")?.ToLowerInvariant();
        var overrideIdeName = ExtractFlag(command, "--override_ide_name")?.ToLowerInvariant();
        var appData = ExtractFlag(command, "--app_data_dir")?.ToLowerInvariant();
        if (ideName is not null || overrideIdeName is not null || appData is not null)
        {
            var matches = markersLower.Any(marker => ideName == marker || overrideIdeName == marker || appData == marker);
            return matches ? 0 : null;
        }

        var commandLower = command.ToLowerInvariant();
        var pathMatches = markersLower.Any(m => commandLower.Contains($"/{m}/") || commandLower.Contains($"\\{m}\\"));
        return pathMatches ? 1 : null;
    }

    public static string Argv0(string command)
    {
        var trimmed = command.TrimStart(' ', '\t');
        if (trimmed.Length == 0) return "";
        var quote = trimmed[0];
        if (quote is '"' or '\'')
        {
            var rest = trimmed[1..];
            var end = rest.IndexOf(quote);
            if (end >= 0) return rest[..end];
        }
        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex >= 0 ? trimmed[..spaceIndex] : trimmed;
    }

    public static bool CommandMatchesProcess(string command, string processNameLower)
    {
        if (processNameLower.Length == 0) return false;

        var exePath = Argv0(command);
        var exeName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (exeName == processNameLower) return true;

        var commandLower = command.ToLowerInvariant();
        if (processNameLower.Length >= 8)
        {
            return exeName.StartsWith($"{processNameLower}_", StringComparison.Ordinal) || commandLower.Contains(processNameLower);
        }
        return commandLower.EndsWith($"\\{processNameLower}", StringComparison.Ordinal)
            || commandLower.EndsWith($"\\{processNameLower}.exe", StringComparison.Ordinal)
            || commandLower.Contains($"\\{processNameLower} ")
            || commandLower.Contains($"\\{processNameLower}.exe ");
    }
}
