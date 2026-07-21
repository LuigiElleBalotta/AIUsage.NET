namespace AIUsage.Core.Providers;

/// <summary>
/// Shared JSONL discovery + incremental parse-cache machinery. Simplified from the Swift edition's
/// actor-based, disk-persisted cache to an in-memory-only cache keyed by path+size+mtime — a pure
/// performance optimization in the original, so dropping the disk persistence changes nothing about
/// correctness (a fresh process just reparses once instead of reusing yesterday's cache).
/// </summary>
public static class JsonlScanning
{
    public sealed record DiscoveredFile(string Path, long Size, DateTimeOffset MTime);

    public static DateTimeOffset SinceDate(int daysBack, DateTimeOffset now)
    {
        return now.AddDays(-daysBack).Date;
    }

    /// <summary>Every *.jsonl regular file under dir (recursively), path-sorted for deterministic dedup order.</summary>
    public static List<DiscoveredFile> JsonlFiles(string dir)
    {
        if (!Directory.Exists(dir)) return new List<DiscoveredFile>();
        var files = new List<DiscoveredFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(path);
                    files.Add(new DiscoveredFile(path, info.Length, info.LastWriteTimeUtc));
                }
                catch
                {
                    // skip files we can't stat
                }
            }
        }
        catch
        {
            return new List<DiscoveredFile>();
        }
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// In-memory incremental JSONL scanner: re-parses only files changed since last scan (path+size+mtime),
/// returns items concatenated in input order. One instance per provider/home identity is expected to be
/// reused across refresh cycles (kept as a singleton on the provider), matching the actor's role in the
/// Swift edition minus disk persistence.
/// </summary>
public sealed class IncrementalJsonlScanner<TItem>
{
    private sealed record CachedFile(long Size, DateTimeOffset MTime, List<TItem> Items);

    private readonly Dictionary<string, Dictionary<string, CachedFile>> _caches = new();
    private readonly object _lock = new();

    public async Task<List<TItem>?> ItemsAsync(
        List<JsonlScanning.DiscoveredFile> files,
        DateTimeOffset since,
        string cacheIdentity,
        Func<byte[], List<TItem>?> parse,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, CachedFile> currentCache;
        lock (_lock)
        {
            currentCache = _caches.TryGetValue(cacheIdentity, out var c) ? c : new Dictionary<string, CachedFile>();
        }

        var nextCache = new Dictionary<string, CachedFile>();
        foreach (var (path, cached) in currentCache)
        {
            if (cached.MTime >= since) nextCache[path] = cached;
        }

        var toParse = new List<JsonlScanning.DiscoveredFile>();
        foreach (var file in files)
        {
            if (file.MTime < since) continue;
            if (currentCache.TryGetValue(file.Path, out var cached) && cached.Size == file.Size && cached.MTime == file.MTime)
            {
                nextCache[file.Path] = cached;
            }
            else
            {
                toParse.Add(file);
            }
        }

        var semaphore = new SemaphoreSlim(8);
        var tasks = toParse.Select(async file =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested || !File.Exists(file.Path)) return (file, (List<TItem>?)null);
                byte[] data;
                try
                {
                    data = await File.ReadAllBytesAsync(file.Path, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return (file, (List<TItem>?)null);
                }
                return (file, parse(data));
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return null;

        foreach (var (file, parsed) in results)
        {
            if (parsed is null) continue;
            nextCache[file.Path] = new CachedFile(file.Size, file.MTime, parsed);
        }

        lock (_lock)
        {
            _caches[cacheIdentity] = nextCache;
        }

        var items = new List<TItem>();
        foreach (var file in files)
        {
            if (nextCache.TryGetValue(file.Path, out var cached)) items.AddRange(cached.Items);
        }
        return items;
    }
}
