using System.Diagnostics;
using System.Text.RegularExpressions;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.OpenCode;

/// <summary>Best-effort answer to "which OpenCode CLI is installed?" — a diagnostic hint only.</summary>
public sealed record OpenCodeVersionHint(string? Version, string? Raw, bool TimedOut = false, string? Error = null)
{
    public string Display => Version ?? (TimedOut ? "unknown (timed out)" : "unknown");
}

/// <summary>
/// Runs <c>opencode --version</c> once per app lifetime and caches the answer. The version is never
/// used to choose a database reader: schemas are detected from sqlite_master instead, so a missing,
/// old, malformed, or timed-out CLI cannot hide readable local usage. Resolution only considers
/// <c>opencode.exe</c> on PATH and known per-user install locations; no shell is involved.
/// </summary>
public sealed class OpenCodeVersionProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly Regex VersionPattern = new(
        @"(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z][0-9A-Za-z.-]*)?)",
        RegexOptions.Compiled);

    private readonly Func<CancellationToken, Task<string?>> _runner;
    private readonly Lazy<Task<OpenCodeVersionHint>> _cached;

    public OpenCodeVersionProbe(Func<CancellationToken, Task<string?>>? runner = null)
    {
        _runner = runner ?? RunDefaultAsync;
        _cached = new Lazy<Task<OpenCodeVersionHint>>(ProbeAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<OpenCodeVersionHint> GetAsync(CancellationToken cancellationToken = default)
    {
        // WaitAsync lets the caller stop waiting without cancelling the shared one-shot probe.
        return _cached.Value.WaitAsync(cancellationToken);
    }

    private async Task<OpenCodeVersionHint> ProbeAsync()
    {
        try
        {
            var raw = await _runner(CancellationToken.None).ConfigureAwait(false);
            return Parse(raw);
        }
        catch (OperationCanceledException)
        {
            return new OpenCodeVersionHint(null, null, TimedOut: true);
        }
        catch (TimeoutException)
        {
            return new OpenCodeVersionHint(null, null, TimedOut: true);
        }
        catch (Exception ex)
        {
            return new OpenCodeVersionHint(null, null, Error: ex.Message);
        }
    }

    private static OpenCodeVersionHint Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new OpenCodeVersionHint(null, null);
        var text = raw.Trim();
        var match = VersionPattern.Match(text);
        var version = match.Success ? match.Groups[1].Value : null;
        if (version is null)
        {
            AppLog.Debug(LogTag.Plugin("opencode"), "opencode --version output did not contain a version");
        }
        return new OpenCodeVersionHint(version, text);
    }

    private static Task<string?> RunDefaultAsync(CancellationToken cancellationToken) =>
        Task.Run(RunDefault, cancellationToken);

    private static string? RunDefault()
    {
        var executable = ResolveExecutable();
        if (executable is null) return null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return null;

            // Start reading immediately so a chatty CLI can never deadlock on a full pipe.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                throw new TimeoutException("opencode --version timed out");
            }

            if (!stdout.Wait(TimeSpan.FromMilliseconds(500)))
            {
                throw new TimeoutException("opencode --version output timed out");
            }
            var output = stdout.Result;
            if (string.IsNullOrWhiteSpace(output) && stderr.Wait(TimeSpan.FromMilliseconds(100)))
            {
                output = stderr.Result;
            }
            return output;
        }
        catch (TimeoutException)
        {
            // Keep the timeout hint; ProbeAsync turns it into TimedOut=true.
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug(LogTag.Plugin("opencode"), $"opencode --version probe failed: {ex.Message}");
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    internal static string? ResolveExecutable(
        string? home = null,
        string? localAppData = null,
        string? pathValue = null,
        Func<string, bool>? fileExists = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        pathValue ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        var exists = fileExists ?? File.Exists;

        var candidates = new List<string>();
        foreach (var directory in pathValue.Split(';'))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length > 0) candidates.Add(Path.Combine(trimmed, "opencode.exe"));
        }
        candidates.Add(Path.Combine(home, ".opencode", "bin", "opencode.exe"));
        candidates.Add(Path.Combine(home, ".local", "bin", "opencode.exe"));
        candidates.Add(Path.Combine(home, ".bun", "bin", "opencode.exe"));
        candidates.Add(Path.Combine(localAppData, "Programs", "opencode", "opencode.exe"));
        candidates.Add(Path.Combine(localAppData, "opencode", "opencode.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (exists(candidate)) return candidate;
            }
            catch
            {
                // An unreadable PATH entry is not a reason to fail the probe.
            }
        }
        return null;
    }
}
