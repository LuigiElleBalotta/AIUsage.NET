namespace AIUsage.Core.Support;

/// <summary>
/// Owns a serial, lock-guarded file appender with single-archive rotation. Direct port of the Swift
/// LogFile: when a write would push the current file past <see cref="DefaultMaxBytes"/> the current
/// file becomes "&lt;name&gt;.1.&lt;ext&gt;" and a fresh file opens in its place — bounding disk usage
/// to roughly 2x the cap while keeping one archive of recent history. An already-oversize file left
/// over from a previous session is rotated once before the first write. If opening/rotating fails the
/// sink disables itself for the rest of the process — logging must never crash the app.
/// </summary>
public sealed class LogFile
{
    public const int DefaultMaxBytes = 10_000_000;

    private readonly string _fileUrl;
    private readonly string _archiveUrl;
    private readonly string _directory;
    private readonly int _maxBytes;

    private readonly object _lock = new();
    private FileStream? _handle;
    private long _size;
    private bool _disabled;
    private bool _opened;

    public string FileUrl => _fileUrl;

    /// <param name="directory">The folder the log file lives in (created on open if missing).</param>
    /// <param name="fileName">The log file name (the archive appends ".1" before the extension).</param>
    /// <param name="maxBytes">Rotation cap; defaults to the 10 MB cap shared with the original.</param>
    public LogFile(string directory, string fileName, int maxBytes = DefaultMaxBytes)
    {
        _directory = directory;
        _fileUrl = Path.Combine(directory, fileName);
        _maxBytes = maxBytes;

        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var archiveName = string.IsNullOrEmpty(ext) ? $"{baseName}.1" : $"{baseName}.1{ext}";
        _archiveUrl = Path.Combine(directory, archiveName);
    }

    /// <summary>Create the directory/file, seed the in-memory size from disk, and perform the
    /// launch-time trim (rotate once if an already-oversize file was left over). Idempotent.</summary>
    public void Open()
    {
        lock (_lock)
        {
            if (_opened) return;
            _opened = true;
            try
            {
                OpenLocked();
            }
            catch (Exception ex)
            {
                FailLocked($"open failed: {ex.Message}");
            }
        }
    }

    /// <summary>Append one already-formatted line (a newline is added). Rotates first if the line
    /// would push the file past the cap. No-op once the sink is disabled.</summary>
    public void Append(string line)
    {
        lock (_lock)
        {
            if (_disabled) return;
            if (!_opened)
            {
                _opened = true;
                try
                {
                    OpenLocked();
                }
                catch (Exception ex)
                {
                    FailLocked($"open failed: {ex.Message}");
                    return;
                }
            }
            if (_handle is null) return;

            var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            if (_size + bytes.Length > _maxBytes)
            {
                try
                {
                    RotateLocked();
                }
                catch (Exception ex)
                {
                    FailLocked($"rotate failed: {ex.Message}");
                    return;
                }
            }
            if (_handle is null) return;
            try
            {
                _handle.Write(bytes, 0, bytes.Length);
                _handle.Flush();
                _size += bytes.Length;
            }
            catch (Exception ex)
            {
                FailLocked($"write failed: {ex.Message}");
            }
        }
    }

    // MARK: - Locked internals (caller holds _lock)

    private void OpenLocked()
    {
        Directory.CreateDirectory(_directory);
        if (!File.Exists(_fileUrl))
        {
            using (File.Create(_fileUrl)) { }
        }

        var handle = new FileStream(_fileUrl, FileMode.Open, FileAccess.Write, FileShare.Read);
        var currentSize = new FileInfo(_fileUrl).Length;
        _handle = handle;
        _size = currentSize;

        if (_size > _maxBytes)
        {
            RotateLocked();
        }
        else
        {
            handle.Seek(0, SeekOrigin.End);
        }
    }

    private void RotateLocked()
    {
        _handle?.Dispose();
        _handle = null;

        if (File.Exists(_archiveUrl)) File.Delete(_archiveUrl);
        if (File.Exists(_fileUrl)) File.Move(_fileUrl, _archiveUrl);

        using (File.Create(_fileUrl)) { }
        _handle = new FileStream(_fileUrl, FileMode.Open, FileAccess.Write, FileShare.Read);
        _size = 0;
    }

    private void FailLocked(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[logfile] File log sink disabled: {message}");
        try { _handle?.Dispose(); } catch { /* ignore */ }
        _handle = null;
        _disabled = true;
    }

    /// <summary>Releases the underlying file handle. Safe to call multiple times; a subsequent
    /// <see cref="Append"/> reopens the file. Mainly useful for tests and clean shutdown.</summary>
    public void Close()
    {
        lock (_lock)
        {
            try { _handle?.Dispose(); } catch { /* ignore */ }
            _handle = null;
            _opened = false;
        }
    }
}
