using System.Text;
using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Pricing;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Claude;

/// <summary>
/// Builds daily token/cost estimates for Claude by scanning Claude Code's local session logs
/// (&lt;config dir&gt;/projects/**/*.jsonl). Direct port of the Swift ClaudeLogUsageScanner, minus the
/// macOS-only Cowork desktop-app sandbox walk (no Windows equivalent exists yet).
/// </summary>
public sealed class ClaudeLogUsageScanner
{
    public sealed class Entry
    {
        public DateTimeOffset Timestamp { get; set; }
        public required TokenBreakdown Tokens { get; set; }
        public string? MessageId { get; set; }
        public string? RequestId { get; set; }
        public bool IsSidechain { get; set; }
        public bool HasSpeed { get; set; }
        public double? CostUSD { get; set; }
        public string? Model { get; set; }
    }

    private readonly IEnvironmentReading _environment;
    private readonly Func<string> _homeDirectory;
    private readonly IncrementalJsonlScanner<Entry> _scanner;
    /// <summary>When set, scanning is pinned to exactly these roots, replacing the normal env/home
    /// resolution — used by extra Claude account cards, which must only ever read their own
    /// CLAUDE_CONFIG_DIR home's logs, never the default account's.</summary>
    private readonly List<string>? _fixedRoots;
    /// <summary>Same-account extra config dirs discovered this launch (see
    /// <see cref="App.ProviderAccountAssembly.DefaultClaudeExtraLogRoots"/>): appended AFTER the
    /// normal env/home resolution, never replacing it — the default card keeps reading whichever
    /// home CLAUDE_CONFIG_DIR/XDG_CONFIG_HOME point at, plus these extra spend-log roots.</summary>
    private readonly List<string> _extraRoots;

    public ClaudeLogUsageScanner(
        IEnvironmentReading? environment = null,
        Func<string>? homeDirectory = null,
        IncrementalJsonlScanner<Entry>? scanner = null,
        List<string>? fixedRoots = null,
        List<string>? extraRoots = null)
    {
        _environment = environment ?? new ProcessEnvironmentReader();
        _homeDirectory = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _scanner = scanner ?? new IncrementalJsonlScanner<Entry>();
        _fixedRoots = fixedRoots;
        _extraRoots = extraRoots ?? new List<string>();
    }

    public async Task<LogUsageScan?> ScanAsync(int daysBack, DateTimeOffset now, ModelPricing pricing, CancellationToken cancellationToken = default)
    {
        var since = JsonlScanning.SinceDate(daysBack, now);
        var roots = ClaudeRoots();
        if (roots.Count == 0) return null;

        var files = UsageFiles(roots);
        if (files.Count == 0) return null;

        var entries = await _scanner.ItemsAsync(files, since, ParseCacheIdentity(), ParseFile, cancellationToken).ConfigureAwait(false);
        if (entries is null) return null;
        return Aggregate(Dedup(entries), since, pricing);
    }

    private string ParseCacheIdentity()
    {
        var home = _homeDirectory();
        var roots = ClaudeRoots().Select(r => r).OrderBy(r => r, StringComparer.Ordinal);
        return $"home={home}\nroots={string.Join("\n", roots)}";
    }

    private List<string> ClaudeRoots()
    {
        var roots = new List<string>();
        var seen = new HashSet<string>();

        void AddIfValid(string path)
        {
            var projects = Path.Combine(path, "projects");
            if (!Directory.Exists(projects)) return;
            if (!seen.Add(path)) return;
            roots.Add(path);
        }

        if (_fixedRoots is not null)
        {
            foreach (var root in _fixedRoots) AddIfValid(root);
            return roots;
        }

        var raw = _environment.Value("CLAUDE_CONFIG_DIR")?.Trim();
        if (!string.IsNullOrEmpty(raw))
        {
            foreach (var part in raw.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                var expanded = PathHelpers.ExpandHome(part);
                if (Path.GetFileName(expanded) == "projects" && Directory.Exists(expanded))
                {
                    expanded = Path.GetDirectoryName(expanded) ?? expanded;
                }
                AddIfValid(expanded);
            }
            if (roots.Count == 0)
            {
                AppLog.Warn(LogTag.Plugin("claude"), $"CLAUDE_CONFIG_DIR is set but contains no Claude data directory with projects/: {raw}");
            }
        }
        else
        {
            var home = _homeDirectory();
            var xdg = _environment.Value("XDG_CONFIG_HOME")?.NilIfEmpty();
            var xdgBase = xdg is not null ? PathHelpers.ExpandHome(xdg) : Path.Combine(home, ".config");
            AddIfValid(Path.Combine(xdgBase, "claude"));
            AddIfValid(Path.Combine(home, ".claude"));
        }
        foreach (var extra in _extraRoots) AddIfValid(PathHelpers.ExpandHome(extra));
        return roots;
    }

    private static List<JsonlScanning.DiscoveredFile> UsageFiles(List<string> roots)
    {
        var files = roots.SelectMany(r => JsonlScanning.JsonlFiles(Path.Combine(r, "projects"))).ToList();
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    // MARK: - Parsing

    public static List<Entry> ParseFile(byte[] data)
    {
        var entries = new List<Entry>();
        var text = Encoding.UTF8.GetString(data);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || !line.Contains("\"usage\":{")) continue;
            if (HasUnsupportedNullField(line)) continue;
            entries.AddRange(ParseEntries(line));
        }
        return entries;
    }

    private static readonly HashSet<string> UnsupportedNullableFields = new()
    {
        "id", "cwd", "model", "speed", "costUSD", "version", "sessionId", "requestId",
        "isApiErrorMessage", "cache_read_input_tokens", "cache_creation_input_tokens"
    };

    public static bool HasUnsupportedNullField(string line)
    {
        const string nullMarker = ":null";
        var offset = 0;
        while (true)
        {
            var idx = line.IndexOf(nullMarker, offset, StringComparison.Ordinal);
            if (idx < 0) return false;
            var fieldEnd = idx > 0 ? idx - 1 : 0;
            if (fieldEnd >= 0 && line[fieldEnd] == '"' && fieldEnd > 0)
            {
                var fieldStart = fieldEnd - 1;
                while (fieldStart > 0 && line[fieldStart] != '"') fieldStart--;
                if (line[fieldStart] == '"')
                {
                    var field = line.Substring(fieldStart + 1, fieldEnd - fieldStart - 1);
                    if (UnsupportedNullableFields.Contains(field)) return true;
                }
            }
            offset = idx + nullMarker.Length;
        }
    }

    private static List<Entry> ParseEntries(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("timestamp", out var tsEl)) return new List<Entry>();
            var timestampRaw = tsEl.GetString();
            if (timestampRaw is null || AIUsageISO8601.Parse(timestampRaw) is not { } timestamp) return new List<Entry>();
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return new List<Entry>();
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return new List<Entry>();
            var parsedUsage = TokenBreakdownFrom(usage);
            if (parsedUsage is null) return new List<Entry>();
            if (!IsValidEntry(root, message)) return new List<Entry>();

            var modelRaw = GetString(message, "model");
            var model = modelRaw == "<synthetic>" ? null : modelRaw;

            var parent = new Entry
            {
                Timestamp = timestamp,
                Tokens = parsedUsage.Value.Tokens,
                MessageId = GetString(message, "id"),
                RequestId = GetString(root, "requestId"),
                IsSidechain = root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True,
                HasSpeed = parsedUsage.Value.HasSpeed,
                CostUSD = root.TryGetProperty("costUSD", out var cu) && cu.ValueKind == JsonValueKind.Number ? cu.GetDouble() : null,
                Model = model
            };

            var entries = new List<Entry> { parent };
            if (usage.TryGetProperty("iterations", out var iterations) && iterations.ValueKind == JsonValueKind.Array)
            {
                var advisorIndex = 0;
                foreach (var iteration in iterations.EnumerateArray())
                {
                    if (iteration.ValueKind != JsonValueKind.Object) continue;
                    if (GetString(iteration, "type") != "advisor_message") continue;
                    var advisorModel = GetString(iteration, "model");
                    if (string.IsNullOrEmpty(advisorModel)) continue;
                    var advisorUsage = TokenBreakdownFrom(iteration);
                    if (advisorUsage is null) continue;

                    entries.Add(new Entry
                    {
                        Timestamp = parent.Timestamp,
                        Tokens = advisorUsage.Value.Tokens,
                        MessageId = parent.MessageId is not null ? $"{parent.MessageId}:advisor:{advisorIndex}" : null,
                        RequestId = parent.RequestId,
                        IsSidechain = parent.IsSidechain,
                        HasSpeed = advisorUsage.Value.HasSpeed,
                        CostUSD = null,
                        Model = advisorModel
                    });
                    advisorIndex++;
                }
            }
            return entries;
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private static (TokenBreakdown Tokens, bool HasSpeed)? TokenBreakdownFrom(JsonElement usage)
    {
        if (!usage.TryGetProperty("input_tokens", out var inputEl) || inputEl.ValueKind != JsonValueKind.Number) return null;
        if (!usage.TryGetProperty("output_tokens", out var outputEl) || outputEl.ValueKind != JsonValueKind.Number) return null;

        string? speed = GetString(usage, "speed");
        if (speed is not null && speed != "fast" && speed != "standard") return null;

        int cacheWrite5m = 0, cacheWrite1h = 0;
        if (usage.TryGetProperty("cache_creation", out var cacheCreation) && cacheCreation.ValueKind == JsonValueKind.Object)
        {
            cacheWrite5m = GetInt(cacheCreation, "ephemeral_5m_input_tokens");
            cacheWrite1h = GetInt(cacheCreation, "ephemeral_1h_input_tokens");
        }
        else
        {
            cacheWrite5m = GetInt(usage, "cache_creation_input_tokens");
        }

        var tokens = new TokenBreakdown
        {
            Input = inputEl.GetInt32(),
            CacheWrite5m = cacheWrite5m,
            CacheWrite1h = cacheWrite1h,
            CacheRead = GetInt(usage, "cache_read_input_tokens"),
            Output = outputEl.GetInt32(),
            IsFast = speed == "fast"
        };
        return (tokens, speed is not null);
    }

    private static bool IsValidEntry(JsonElement root, JsonElement message)
    {
        var version = GetString(root, "version");
        if (version is not null && !IsSemverPrefix(version)) return false;
        foreach (var value in new[] { GetString(root, "sessionId"), GetString(root, "requestId"), GetString(message, "id"), GetString(message, "model") })
        {
            if (value is { Length: 0 }) return false;
        }
        return true;
    }

    public static bool IsSemverPrefix(string value)
    {
        var i = 0;
        bool Digits()
        {
            var start = i;
            while (i < value.Length && char.IsAsciiDigit(value[i])) i++;
            return i > start;
        }
        if (!Digits() || i >= value.Length || value[i] != '.') return false;
        i++;
        if (!Digits() || i >= value.Length || value[i] != '.') return false;
        i++;
        return i < value.Length && char.IsAsciiDigit(value[i]);
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    // MARK: - Dedup

    private sealed record ExactKey(string MessageId, string? RequestId);

    public static List<Entry> Dedup(List<Entry> entries)
    {
        var deduped = new List<Entry>();
        var exactIndex = new Dictionary<ExactKey, int>();
        var messageIndex = new Dictionary<string, List<int>>();

        foreach (var entry in entries)
        {
            if (entry.MessageId is null)
            {
                deduped.Add(entry);
                continue;
            }
            var key = new ExactKey(entry.MessageId, entry.RequestId);
            int? collision = exactIndex.TryGetValue(key, out var exact) ? exact : null;
            if (collision is null && messageIndex.TryGetValue(entry.MessageId, out var indices))
            {
                collision = indices.FirstOrDefault(idx => entry.IsSidechain || deduped[idx].IsSidechain, -1);
                if (collision == -1) collision = null;
            }

            if (collision is { } index)
            {
                if (ShouldReplace(entry, deduped[index]))
                {
                    var old = deduped[index];
                    if (old.MessageId is not null) exactIndex.Remove(new ExactKey(old.MessageId, old.RequestId));
                    deduped[index] = entry;
                    exactIndex[key] = index;
                }
                continue;
            }

            var newIndex = deduped.Count;
            deduped.Add(entry);
            exactIndex[key] = newIndex;
            if (!messageIndex.TryGetValue(entry.MessageId, out var list))
            {
                list = new List<int>();
                messageIndex[entry.MessageId] = list;
            }
            list.Add(newIndex);
        }
        return deduped;
    }

    public static bool ShouldReplace(Entry candidate, Entry existing)
    {
        if (candidate.IsSidechain != existing.IsSidechain) return existing.IsSidechain;
        var candidateTotal = candidate.Tokens.TotalTokens;
        var existingTotal = existing.Tokens.TotalTokens;
        if (candidateTotal != existingTotal) return candidateTotal > existingTotal;
        return candidate.HasSpeed && !existing.HasSpeed;
    }

    // MARK: - Aggregation

    public static LogUsageScan Aggregate(List<Entry> entries, DateTimeOffset since, ModelPricing pricing)
    {
        var accumulator = new DailyUsageAccumulator();
        foreach (var entry in entries)
        {
            if (entry.Timestamp < since) continue;
            var day = DailyUsageAccumulator.DayKey(entry.Timestamp);
            var trimmedModel = entry.Model?.Trim().NilIfEmpty();
            var modelName = trimmedModel ?? ModelUsageEntry.UnattributedModelName;

            double cost;
            if (entry.CostUSD is { } carried)
            {
                cost = carried;
            }
            else if (trimmedModel is not null && pricing.EstimatedCostDollars(trimmedModel, entry.Tokens) is { } estimated)
            {
                cost = estimated;
            }
            else
            {
                if (trimmedModel is not null && entry.Tokens.TotalTokens > 0)
                {
                    accumulator.AddUnknownModel(day, trimmedModel);
                }
                continue;
            }

            accumulator.Add(day, entry.Tokens.TotalTokens, cost, modelName);
        }
        return accumulator.Build();
    }
}
