using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Core.Models;
using AIUsage.Core.Pricing;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Codex;

/// <summary>
/// Builds daily token/cost estimates for Codex by scanning the Codex CLI's local session rollouts
/// ($CODEX_HOME/sessions/**/*.jsonl + archived_sessions/). Direct port of the Swift
/// CodexLogUsageScanner (ccusage-equivalent semantics), minus the disk-persisted parse cache (see
/// PORTING_NOTES.md — in-memory only here).
/// </summary>
public sealed class CodexLogUsageScanner
{
    public sealed class Event
    {
        public DateTimeOffset Timestamp { get; set; }
        public required string Model { get; set; }
        public int Input { get; set; }
        public int Cached { get; set; }
        public int Output { get; set; }
        public int Reasoning { get; set; }
        public int Total { get; set; }
        public bool IsFast { get; set; }
    }

    private readonly IEnvironmentReading _environment;
    private readonly Func<string> _homeDirectory;
    private readonly IncrementalJsonlScanner<Event> _scanner;

    public CodexLogUsageScanner(
        IEnvironmentReading? environment = null,
        Func<string>? homeDirectory = null,
        IncrementalJsonlScanner<Event>? scanner = null)
    {
        _environment = environment ?? new ProcessEnvironmentReader();
        _homeDirectory = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _scanner = scanner ?? new IncrementalJsonlScanner<Event>();
    }

    public async Task<LogUsageScan?> ScanAsync(int daysBack, DateTimeOffset now, ModelPricing pricing, CancellationToken cancellationToken = default)
    {
        var homes = CodexHomes();
        var since = JsonlScanning.SinceDate(daysBack, now);
        var identityPaths = homes.Select(Path.GetFullPath).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        var identity = identityPaths.Count == 0 ? "no-codex-home" : string.Join("\n", identityPaths);

        var files = SessionFiles(homes);
        if (files.Count == 0) return null;

        var events = await _scanner.ItemsAsync(files, since, identity, ParseFile, cancellationToken).ConfigureAwait(false);
        if (events is null) return null;
        return Aggregate(events, since, pricing);
    }

    private List<string> CodexHomes()
    {
        var raw = _environment.Value("CODEX_HOME")?.Trim();
        if (!string.IsNullOrEmpty(raw))
        {
            return raw.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).Select(PathHelpers.ExpandHome).ToList();
        }
        return new List<string> { Path.Combine(_homeDirectory(), ".codex") };
    }

    private static List<JsonlScanning.DiscoveredFile> SessionFiles(List<string> homes)
    {
        var files = new List<JsonlScanning.DiscoveredFile>();
        var seenDirs = new HashSet<string>();
        foreach (var home in homes)
        {
            var seenRelative = new HashSet<string>();
            var sourceDirs = new List<string>();
            foreach (var name in new[] { "sessions", "archived_sessions" })
            {
                var dir = Path.Combine(home, name);
                if (Directory.Exists(dir)) sourceDirs.Add(dir);
            }
            if (sourceDirs.Count == 0) sourceDirs.Add(home);

            foreach (var dir in sourceDirs)
            {
                var fullDir = Path.GetFullPath(dir);
                if (!seenDirs.Add(fullDir)) continue;
                foreach (var file in JsonlScanning.JsonlFiles(fullDir))
                {
                    var relative = file.Path.Length > fullDir.Length ? file.Path[fullDir.Length..] : file.Path;
                    if (!seenRelative.Add(relative)) continue;
                    files.Add(file);
                }
            }
        }
        return files;
    }

    // MARK: - Parsing

    private sealed class RawUsage
    {
        public int Input;
        public int Cached;
        public int Output;
        public int Reasoning;
        public int Total;

        public static RawUsage FromJson(JsonElement json)
        {
            int? IntOf(params string[] keys)
            {
                foreach (var key in keys)
                {
                    if (json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt32();
                }
                return null;
            }
            var input = IntOf("input_tokens", "prompt_tokens", "input") ?? 0;
            var cached = IntOf("cached_input_tokens", "cache_read_input_tokens", "cached_tokens") ?? 0;
            var output = IntOf("output_tokens", "completion_tokens", "output") ?? 0;
            var reasoning = IntOf("reasoning_output_tokens", "reasoning_tokens") ?? 0;
            var reported = IntOf("total_tokens") ?? 0;
            var recomputed = input + output + reasoning;
            var total = (reported > 0 || recomputed == 0) ? reported : recomputed;
            return new RawUsage { Input = input, Cached = cached, Output = output, Reasoning = reasoning, Total = total };
        }

        public bool EqualCounts(RawUsage other) =>
            Input == other.Input && Cached == other.Cached && Output == other.Output && Reasoning == other.Reasoning && Total == other.Total;

        public RawUsage Subtracting(RawUsage? previous) => new()
        {
            Input = Math.Max(0, Input - (previous?.Input ?? 0)),
            Cached = Math.Max(0, Cached - (previous?.Cached ?? 0)),
            Output = Math.Max(0, Output - (previous?.Output ?? 0)),
            Reasoning = Math.Max(0, Reasoning - (previous?.Reasoning ?? 0)),
            Total = Math.Max(0, Total - (previous?.Total ?? 0))
        };
    }

    private abstract record ChildReplayGate
    {
        public sealed record UntilStartedAt(double Gate) : ChildReplayGate;
        public sealed record UntilSelfTimedTaskStarted : ChildReplayGate;

        public bool IsCleared(double startedAt, string? lineTimestamp) => this switch
        {
            UntilStartedAt g => startedAt >= g.Gate,
            UntilSelfTimedTaskStarted => lineTimestamp is not null
                && AIUsageISO8601.Parse(lineTimestamp.Trim()) is { } lineDate
                && startedAt >= Math.Floor((double)lineDate.ToUnixTimeSeconds()),
            _ => false
        };
    }

    public static List<Event> ParseFile(byte[] data)
    {
        var events = new List<Event>();
        RawUsage? previousTotals = null;
        string? currentModel = null;
        var currentTierIsFast = false;
        var sawSessionMeta = false;
        ChildReplayGate? replayGate = null;

        var text = Encoding.UTF8.GetString(data);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            var isTurnContext = line.Contains("\"type\":\"turn_context\"");
            var isSessionMeta = !sawSessionMeta && line.Contains("\"type\":\"session_meta\"");
            var isTaskStarted = replayGate is not null && line.Contains("\"type\":\"task_started\"");
            var isThreadSettings = line.Contains("\"type\":\"thread_settings_applied\"");
            var isTokenCount = line.Contains("\"type\":\"token_count\"");
            if (!isTurnContext && !isSessionMeta && !isTaskStarted && !isThreadSettings && !isTokenCount) continue;

            JsonElement root;
            try { root = JsonDocument.Parse(line).RootElement; } catch { continue; }
            if (root.ValueKind != JsonValueKind.Object) continue;

            var type = GetString(root, "type");
            var hasPayload = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object;

            if (type == "turn_context")
            {
                if (hasPayload && ModelNameIn(payload) is { } m) currentModel = m;
                continue;
            }

            if (type == "session_meta" && !sawSessionMeta)
            {
                sawSessionMeta = true;
                if (hasPayload && IsChildSessionMeta(payload))
                {
                    var timestampRaw = GetString(root, "timestamp")?.Trim();
                    if (timestampRaw is not null && AIUsageISO8601.Parse(timestampRaw) is { } created)
                    {
                        replayGate = new ChildReplayGate.UntilStartedAt(Math.Floor((double)created.ToUnixTimeSeconds()));
                    }
                    else
                    {
                        replayGate = new ChildReplayGate.UntilSelfTimedTaskStarted();
                    }
                }
                continue;
            }

            if (isThreadSettings && type == "event_msg" && hasPayload && GetString(payload, "type") == "thread_settings_applied")
            {
                if (ServiceTier(payload) is { } tier) currentTierIsFast = tier == "fast" || tier == "priority";
                continue;
            }

            if (type != "event_msg" || !hasPayload) continue;

            if (GetString(payload, "type") == "task_started")
            {
                if (replayGate is { } gate && payload.TryGetProperty("started_at", out var sa) && sa.ValueKind == JsonValueKind.Number)
                {
                    if (gate.IsCleared(sa.GetDouble(), GetString(root, "timestamp"))) replayGate = null;
                }
                continue;
            }

            if (GetString(payload, "type") != "token_count") continue;
            var timestampRaw2 = GetString(root, "timestamp")?.Trim();
            if (timestampRaw2 is null || AIUsageISO8601.Parse(timestampRaw2) is not { } timestamp) continue;

            JsonElement? info = payload.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object ? infoEl : null;
            RawUsage? totals = info is { } i && i.TryGetProperty("total_token_usage", out var tt) && tt.ValueKind == JsonValueKind.Object
                ? RawUsage.FromJson(tt) : null;

            if (replayGate is not null)
            {
                if (totals is not null) previousTotals = totals;
                continue;
            }

            if (totals is not null && previousTotals is not null && totals.EqualCounts(previousTotals))
            {
                continue;
            }

            RawUsage usage;
            if (info is { } i2 && i2.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
            {
                usage = RawUsage.FromJson(last);
            }
            else if (totals is not null)
            {
                usage = totals.Subtracting(previousTotals);
            }
            else
            {
                continue;
            }
            if (totals is not null) previousTotals = totals;
            if (usage.Input <= 0 && usage.Cached <= 0 && usage.Output <= 0 && usage.Reasoning <= 0) continue;

            var parsedModel = ModelNameIn(payload) ?? (info is { } i3 ? ModelNameIn(i3) : null);
            var model = ResolveModel(parsedModel, timestampRaw2, ref currentModel);

            events.Add(new Event
            {
                Timestamp = timestamp,
                Model = model,
                Input = usage.Input,
                Cached = Math.Min(usage.Cached, usage.Input),
                Output = usage.Output,
                Reasoning = usage.Reasoning,
                Total = usage.Total,
                IsFast = currentTierIsFast
            });
        }
        return events;
    }

    private static string? ServiceTier(JsonElement payload)
    {
        JsonElement? settings = payload.TryGetProperty("thread_settings", out var s) && s.ValueKind == JsonValueKind.Object ? s : null;
        foreach (var candidate in new[] { settings, payload })
        {
            if (candidate is { } c && c.TryGetProperty("service_tier", out var v) && v.ValueKind == JsonValueKind.String)
            {
                var text = v.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        return null;
    }

    private static string? ModelNameIn(JsonElement json)
    {
        foreach (var key in new[] { "model", "model_name" })
        {
            if (json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var text = v.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        if (json.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("model", out var mv) && mv.ValueKind == JsonValueKind.String)
        {
            var text = mv.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    public static bool IsChildSessionMeta(JsonElement payload)
    {
        if (HasNonNullValue(payload, "forked_from_id")) return true;
        if (HasNonNullValue(payload, "parent_thread_id")) return true;
        if (GetString(payload, "thread_source") == "subagent") return true;
        if (payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object && HasNonNullValue(source, "subagent")) return true;
        return false;
    }

    private static bool HasNonNullValue(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            _ => true
        };
    }

    public static string ResolveModel(string? parsed, string timestamp, ref string? currentModel)
    {
        if (parsed is not null) currentModel = parsed;
        string model;
        if (parsed is not null) model = parsed;
        else if (currentModel is not null) model = currentModel;
        else { currentModel = "gpt-5"; model = "gpt-5"; }

        if (model == AutoReviewModel) model = AutoReviewFallback(timestamp);
        return model;
    }

    private const string AutoReviewModel = "codex-auto-review";

    private static readonly (string ReleasedOn, string Model)[] AutoReviewFallbacks =
    {
        ("2026-04-23", "gpt-5.5"),
        ("2026-03-05", "gpt-5.4"),
        ("2026-02-05", "gpt-5.3-codex"),
        ("2025-12-11", "gpt-5.2-codex"),
        ("2025-11-13", "gpt-5.1-codex"),
        ("2025-09-15", "gpt-5-codex"),
        ("2025-08-07", "gpt-5")
    };

    public static string AutoReviewFallback(string timestamp)
    {
        var date = timestamp.Length >= 10 ? timestamp[..10] : timestamp;
        if (date.Length != 10 || !Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$")) return "gpt-5";
        foreach (var (releasedOn, model) in AutoReviewFallbacks)
        {
            if (string.CompareOrdinal(date, releasedOn) >= 0) return model;
        }
        return "gpt-5";
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // MARK: - Aggregation

    private sealed record EventKey(DateTimeOffset Timestamp, string Model, int Input, int Cached, int Output, int Reasoning, int Total);

    public static LogUsageScan Aggregate(List<Event> events, DateTimeOffset since, ModelPricing pricing)
    {
        var seen = new HashSet<EventKey>();
        var accumulator = new DailyUsageAccumulator();

        foreach (var evt in events)
        {
            if (evt.Timestamp < since) continue;
            var key = new EventKey(evt.Timestamp, evt.Model, evt.Input, evt.Cached, evt.Output, evt.Reasoning, evt.Total);
            if (!seen.Add(key)) continue;

            var day = DailyUsageAccumulator.DayKey(evt.Timestamp);
            var trimmedModel = evt.Model.Trim().NilIfEmpty();
            if (trimmedModel is null) continue;

            var canonicalModel = pricing.Supplement.CanonicalName(trimmedModel) ?? trimmedModel;
            var isFastAlias = canonicalModel.EndsWith("-fast", StringComparison.Ordinal);
            var rateModel = isFastAlias ? canonicalModel[..^"-fast".Length] : canonicalModel;

            var baseRates = pricing.Resolve(rateModel);
            var resolvedRates = baseRates ?? pricing.Resolve(trimmedModel);
            if (resolvedRates is null)
            {
                if (evt.Total > 0) accumulator.AddUnknownModel(day, trimmedModel);
                continue;
            }
            var appliesCodexFastTier = isFastAlias ? baseRates is not null : evt.IsFast;
            var eventCost = Cost(resolvedRates, evt, rateModel, appliesCodexFastTier, CodexPriorityMultiplier(rateModel, resolvedRates));
            accumulator.Add(day, evt.Total, eventCost, trimmedModel);
        }

        return accumulator.Build();
    }

    public static double Cost(ModelRates rates, Event evt, string model, bool fastTier, double fastMultiplier)
    {
        var effective = rates;
        if (CodexLongContextRates(model) is { } longContext)
        {
            effective = effective with
            {
                InputAbove200kPerMillion = longContext.Input,
                OutputAbove200kPerMillion = longContext.Output,
                CacheReadAbove200kPerMillion = longContext.CacheRead,
                LongContextThresholdTokens = 272_000
            };
        }
        if (CodexModelHasNoCacheDiscount(model))
        {
            effective = effective with { CacheReadPerMillion = effective.InputPerMillion, CacheReadAbove200kPerMillion = effective.InputAbove200kPerMillion };
        }
        else if (!rates.CacheReadIsExplicit)
        {
            effective = effective with { CacheReadPerMillion = effective.InputPerMillion, CacheReadAbove200kPerMillion = effective.InputAbove200kPerMillion };
        }
        effective = effective with { FastMultiplier = fastMultiplier };

        var nonCached = Math.Max(0, evt.Input - evt.Cached);
        return ModelRatesExtensions.CostDollars(effective, new TokenBreakdown
        {
            Input = nonCached,
            CacheRead = evt.Cached,
            Output = evt.Output,
            IsFast = fastTier
        });
    }

    private static double CodexPriorityMultiplier(string model, ModelRates rates)
    {
        var basev = DatedBaseModel(model);
        return basev switch
        {
            "gpt-5.5" or "gpt-5.5-pro" => 2.5,
            "gpt-5.4" or "gpt-5.4-pro" or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna" => 2,
            _ => rates.FastMultiplier == 1 ? 2 : rates.FastMultiplier
        };
    }

    private static bool CodexModelHasNoCacheDiscount(string model) => DatedBaseModel(model) switch
    {
        "gpt-5.4-pro" or "gpt-5.5-pro" => true,
        _ => false
    };

    private static (double Input, double Output, double CacheRead)? CodexLongContextRates(string model) => DatedBaseModel(model) switch
    {
        "gpt-5.4" => (5, 22.5, 0.5),
        "gpt-5.4-pro" => (60, 270, 60),
        "gpt-5.5" => (10, 45, 1),
        "gpt-5.5-pro" => (60, 270, 60),
        "gpt-5.6-sol" => (10, 45, 1),
        "gpt-5.6-terra" => (5, 22.5, 0.5),
        "gpt-5.6-luna" => (2, 9, 0.2),
        _ => null
    };

    private static readonly Regex DatedSuffix1 = new(@"-\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly Regex DatedSuffix2 = new(@"-\d{8}$", RegexOptions.Compiled);

    private static string DatedBaseModel(string model) => DatedSuffix2.Replace(DatedSuffix1.Replace(model, ""), "");
}
