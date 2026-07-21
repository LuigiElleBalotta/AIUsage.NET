using System.Text;

namespace AIUsage.Core.Support;

/// <summary>
/// Subsystem tags prefixing every log line (grep-friendly: [refresh], [cache], [plugin:claude], ...).
/// </summary>
public static class LogTag
{
    public const string Refresh = "refresh";
    public const string Cache = "cache";
    public const string Http = "http";
    public const string Auth = "auth";
    public const string CredentialStore = "credentialstore";
    public const string TrayIcon = "trayicon";
    public const string Updates = "updates";
    public const string Config = "config";
    public const string StatusItem = "statusitem";
    public const string LocalApi = "localapi";
    public const string Subprocess = "subprocess";
    public const string Lifecycle = "lifecycle";
    public const string Notifications = "notifications";
    public const string Pricing = "pricing";

    public static string Plugin(string id) => $"plugin:{id}";
    public static string AuthFor(string id) => $"auth:{id}";
}

/// <summary>
/// One consolidated logging facility. Mirrors the Swift AppLog: a single level floor governs both the
/// file sink and (on Windows) an ETW/Debug output, with error/warn/info/debug severities.
/// </summary>
public static class AppLog
{
    public enum Level
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3
    }

    private static readonly object LevelLock = new();
    private static Level _currentLevel = Level.Info;

    private static readonly object FileLock = new();
    private static string? _logFilePath;

    public static void Bootstrap(string logFilePath, Level level = Level.Info)
    {
        _logFilePath = logFilePath;
        SetLevel(level);
        try
        {
            var dir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch
        {
            // best effort; logging must never crash the app
        }
        Info(LogTag.Config, $"AIUsage starting (level={level}, log={logFilePath})");
    }

    public static void SetLevel(Level level)
    {
        lock (LevelLock) { _currentLevel = level; }
    }

    public static Level CurrentLevel
    {
        get { lock (LevelLock) { return _currentLevel; } }
    }

    public static void Error(string tag, string message) => Emit(Level.Error, tag, message);
    public static void Warn(string tag, string message) => Emit(Level.Warn, tag, message);
    public static void Info(string tag, string message) => Emit(Level.Info, tag, message);
    public static void Debug(string tag, string message) => Emit(Level.Debug, tag, message);

    private static void Emit(Level level, string tag, string message)
    {
        if ((int)level > (int)CurrentLevel) return;

        var redacted = LogRedaction.RedactLogMessage(message);
        var line = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level.ToString().ToUpperInvariant()}] [{tag}] {redacted}";

        System.Diagnostics.Debug.WriteLine(line);

        if (_logFilePath is null) return;
        try
        {
            lock (FileLock)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // never throw from logging
        }
    }
}
