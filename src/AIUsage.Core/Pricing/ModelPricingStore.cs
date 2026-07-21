using System.Text.Json;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Pricing;

/// <summary>
/// Owns the app's model pricing data: bundled snapshots for offline first launch, on-disk caches, and
/// hourly refreshes from the live feeds. Direct port of the Swift ModelPricingStore (actor -&gt; a lock-
/// guarded singleton, since C# has no built-in actor isolation).
/// </summary>
public sealed class ModelPricingStore
{
    public static readonly ModelPricingStore Shared = new();

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(30);

    private enum SourceId
    {
        LiteLlm,
        ModelsDev,
        Supplement
    }

    private sealed class SourceState
    {
        public string? ETag { get; set; }
        public DateTimeOffset? FetchedAt { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
    }

    private readonly IHttpClient _http;
    private readonly string _cacheDirectory;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<SourceId, Uri> _sourceUrls;
    private readonly Func<string, byte[]?> _bundledData;

    private readonly object _lock = new();
    private bool _loaded;
    private ModelPricing _pricing = ModelPricing.Empty;
    private Dictionary<SourceId, SourceState> _sourceStates = new();
    private Task? _refreshTask;

    private ModelPricingStore(
        IHttpClient? http = null,
        string? cacheDirectory = null,
        Func<DateTimeOffset>? now = null,
        Dictionary<SourceId, Uri>? sourceUrls = null,
        Func<string, byte[]?>? bundledData = null)
    {
        _http = http ?? new SystemHttpClient();
        _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _sourceUrls = sourceUrls ?? DefaultSourceUrls();
        _bundledData = bundledData ?? BundledResourceData;
    }

    private static Dictionary<SourceId, Uri> DefaultSourceUrls() => new()
    {
        [SourceId.LiteLlm] = new Uri("https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json"),
        [SourceId.ModelsDev] = new Uri("https://models.dev/api.json"),
        [SourceId.Supplement] = new Uri("https://robinebers.github.io/openusage/pricing_supplement.json")
    };

    private static string DefaultCacheDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsage", "pricing");

    private static byte[]? BundledResourceData(string resourceName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", $"{resourceName}.json");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public ModelPricing Current()
    {
        LoadIfNeeded();
        lock (_lock)
        {
            if (_refreshTask is null && Enum.GetValues<SourceId>().Any(IsDueUnlocked))
            {
                _refreshTask = Task.Run(RefreshDueSourcesAsync);
            }
            return _pricing;
        }
    }

    public async Task RefreshNowAsync()
    {
        LoadIfNeeded();
        Task? task;
        lock (_lock)
        {
            _refreshTask ??= Task.Run(RefreshDueSourcesAsync);
            task = _refreshTask;
        }
        await task.ConfigureAwait(false);
    }

    private void LoadIfNeeded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            _sourceStates = ReadSourceStates();
            RebuildPricing();
        }
    }

    private void RebuildPricing()
    {
        _pricing = new ModelPricing(
            LoadSupplement(),
            LoadCatalog(SourceId.LiteLlm, PricingCatalogCodecs.CatalogFromCompact),
            LoadCatalog(SourceId.ModelsDev, PricingCatalogCodecs.CatalogFromCompact));
    }

    private PricingSupplement LoadSupplement()
    {
        var cached = ReadCache(SourceId.Supplement);
        if (cached is not null)
        {
            try
            {
                return PricingSupplement.Decode(cached);
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogTag.Pricing, $"cached supplement unreadable, using bundled: {ex.Message}");
            }
        }
        var bundled = _bundledData("pricing_supplement");
        if (bundled is null)
        {
            AppLog.Error(LogTag.Pricing, "bundled pricing_supplement.json missing");
            return new PricingSupplement();
        }
        try
        {
            return PricingSupplement.Decode(bundled);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogTag.Pricing, $"bundled pricing_supplement.json unreadable: {ex.Message}");
            return new PricingSupplement();
        }
    }

    private PricingCatalog LoadCatalog(SourceId source, Func<byte[], PricingCatalog> parse)
    {
        var catalog = new PricingCatalog();
        var resourceName = source == SourceId.LiteLlm ? "pricing_litellm_snapshot" : "pricing_models_dev_snapshot";
        var bundled = _bundledData(resourceName);
        if (bundled is not null)
        {
            try
            {
                catalog = PricingCatalogCodecs.CatalogFromCompact(bundled);
            }
            catch (Exception ex)
            {
                AppLog.Error(LogTag.Pricing, $"bundled {resourceName}.json unreadable: {ex.Message}");
            }
        }
        else
        {
            AppLog.Error(LogTag.Pricing, $"bundled {resourceName}.json missing");
        }

        var cached = ReadCache(source);
        if (cached is not null)
        {
            try
            {
                catalog = catalog.Merging(parse(cached));
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogTag.Pricing, $"cached {source} catalog unreadable, using bundled: {ex.Message}");
            }
        }
        return catalog;
    }

    private bool IsDueUnlocked(SourceId source)
    {
        var state = _sourceStates.GetValueOrDefault(source) ?? new SourceState();
        if (state.FailedAt is { } failedAt && _now() - failedAt < FailureRetryInterval) return false;
        if (state.FetchedAt is not { } fetchedAt) return true;
        return _now() - fetchedAt >= RefreshInterval;
    }

    private async Task RefreshDueSourcesAsync()
    {
        try
        {
            var changed = false;
            foreach (var source in Enum.GetValues<SourceId>())
            {
                bool due;
                lock (_lock) { due = IsDueUnlocked(source); }
                if (!due) continue;
                if (await FetchAsync(source).ConfigureAwait(false)) changed = true;
            }
            if (changed)
            {
                lock (_lock)
                {
                    RebuildPricing();
                    AppLog.Info(LogTag.Pricing, $"pricing refreshed ({_pricing.Primary.Entries.Count} LiteLLM, {_pricing.Secondary.Entries.Count} models.dev, {_pricing.Supplement.Pricing.Count} supplement models)");
                }
            }
            WriteSourceStates();
        }
        finally
        {
            lock (_lock) { _refreshTask = null; }
        }
    }

    private async Task<bool> FetchAsync(SourceId source)
    {
        if (!_sourceUrls.TryGetValue(source, out var url)) return false;
        var state = _sourceStates.GetValueOrDefault(source) ?? new SourceState();
        var request = new HttpRequestSpec
        {
            Method = "GET",
            Url = url,
            Timeout = TimeSpan.FromSeconds(30),
            Headers = state.ETag is not null ? new Dictionary<string, string> { ["If-None-Match"] = state.ETag } : new()
        };
        try
        {
            var response = await _http.SendAsync(request).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case 200:
                    var cacheData = ValidatedCacheData(source, response.Body);
                    WriteCache(source, cacheData);
                    state.ETag = response.Header("etag");
                    state.FetchedAt = _now();
                    state.FailedAt = null;
                    lock (_lock) { _sourceStates[source] = state; }
                    return true;
                case 304:
                    state.FetchedAt = _now();
                    state.FailedAt = null;
                    lock (_lock) { _sourceStates[source] = state; }
                    return false;
                default:
                    throw new InvalidOperationException($"HTTP {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            state.FailedAt = _now();
            lock (_lock) { _sourceStates[source] = state; }
            AppLog.Warn(LogTag.Pricing, $"{source} refresh failed, keeping cached data: {ex.Message}");
            return false;
        }
    }

    private byte[] ValidatedCacheData(SourceId source, byte[] body)
    {
        switch (source)
        {
            case SourceId.LiteLlm:
                return PricingCatalogCodecs.CompactData(PricingCatalogCodecs.CatalogFromLiteLLM(body));
            case SourceId.ModelsDev:
                return PricingCatalogCodecs.CompactData(PricingCatalogCodecs.CatalogFromModelsDev(body));
            case SourceId.Supplement:
                PricingSupplement.Decode(body); // validate
                return body;
            default:
                throw new InvalidOperationException("unknown source");
        }
    }

    private string CacheFile(SourceId source) => Path.Combine(_cacheDirectory, $"{source.ToString().ToLowerInvariant()}.json");
    private string StateFile => Path.Combine(_cacheDirectory, "state.json");

    private byte[]? ReadCache(SourceId source)
    {
        var path = CacheFile(source);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private void WriteCache(SourceId source, byte[] data)
    {
        Directory.CreateDirectory(_cacheDirectory);
        File.WriteAllBytes(CacheFile(source), data);
    }

    private Dictionary<SourceId, SourceState> ReadSourceStates()
    {
        try
        {
            if (!File.Exists(StateFile)) return new();
            var data = File.ReadAllBytes(StateFile);
            var wire = JsonSerializer.Deserialize<Dictionary<string, WireState>>(data, JsonDefaults.Options);
            if (wire is null) return new();
            var result = new Dictionary<SourceId, SourceState>();
            foreach (var (key, value) in wire)
            {
                if (Enum.TryParse<SourceId>(key, true, out var id))
                {
                    result[id] = new SourceState { ETag = value.ETag, FetchedAt = value.FetchedAt, FailedAt = value.FailedAt };
                }
            }
            return result;
        }
        catch
        {
            return new();
        }
    }

    private void WriteSourceStates()
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            Dictionary<SourceId, SourceState> snapshot;
            lock (_lock) { snapshot = new Dictionary<SourceId, SourceState>(_sourceStates); }
            var wire = snapshot.ToDictionary(kv => kv.Key.ToString(), kv => new WireState { ETag = kv.Value.ETag, FetchedAt = kv.Value.FetchedAt, FailedAt = kv.Value.FailedAt });
            File.WriteAllBytes(StateFile, JsonSerializer.SerializeToUtf8Bytes(wire));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogTag.Pricing, $"could not persist pricing fetch state: {ex.Message}");
        }
    }

    private sealed class WireState
    {
        public string? ETag { get; set; }
        public DateTimeOffset? FetchedAt { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
    }
}
