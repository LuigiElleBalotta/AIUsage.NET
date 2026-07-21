using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Pricing;
using AIUsage.Core.Providers;
using AIUsage.Core.Services;
using AIUsage.Core.Support;

namespace AIUsage.Core.Providers.Grok;

/// <summary>
/// Builds daily token/cost estimates from the Grok CLI's local log (~/.grok/logs/unified.jsonl).
/// Direct port of the Swift GrokLogUsageScanner.
/// </summary>
public sealed class GrokLogUsageScanner
{
    private readonly ITextFileAccessing _files;
    private readonly IEnvironmentReading _environment;
    private readonly Func<string> _homeDirectory;

    public GrokLogUsageScanner(ITextFileAccessing? files = null, IEnvironmentReading? environment = null, Func<string>? homeDirectory = null)
    {
        _files = files ?? new LocalTextFileAccessor();
        _environment = environment ?? new ProcessEnvironmentReader();
        _homeDirectory = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public string LogPath
    {
        get
        {
            var raw = _environment.Value("GROK_HOME")?.Trim();
            if (!string.IsNullOrEmpty(raw))
            {
                return Path.Combine(PathHelpers.ExpandHome(raw).TrimmingTrailingSlashes(), "logs", "unified.jsonl");
            }
            return Path.Combine(_homeDirectory(), ".grok", "logs", "unified.jsonl");
        }
    }

    public async Task<LogUsageScan?> ScanAsync(int daysBack, DateTimeOffset now, ModelPricing pricing, CancellationToken cancellationToken = default)
    {
        var path = LogPath;
        if (!_files.Exists(path)) return null;
        string text;
        try
        {
            text = await Task.Run(() => _files.ReadText(path), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        return Parse(text, JsonlScanning.SinceDate(daysBack, now), pricing);
    }

    public static LogUsageScan Parse(string text, DateTimeOffset since, ModelPricing pricing)
    {
        var modelByPid = new Dictionary<int, string>();
        var accumulator = new DailyUsageAccumulator();

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (!line.Contains("inference_done") && !line.Contains("model")) continue;

            JsonElement obj;
            try { obj = JsonDocument.Parse(line).RootElement; } catch { continue; }
            if (obj.ValueKind != JsonValueKind.Object) continue;
            if (!obj.TryGetProperty("msg", out var msgEl) || msgEl.ValueKind != JsonValueKind.String) continue;
            var msg = msgEl.GetString()!;

            var ctx = obj.TryGetProperty("ctx", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Object ? ctxEl : default;
            int? pid = obj.TryGetProperty("pid", out var pidEl) ? (int?)ProviderParse.Number(pidEl) : null;

            var model = ModelId(msg, ctx);
            if (model is not null)
            {
                if (pid is { } p) modelByPid[p] = model;
                continue;
            }

            if (msg != "shell.turn.inference_done") continue;
            if (ProviderParse.Number(GetOrNull(ctx, "prompt_tokens")) is not { } promptTokens) continue;
            if (!obj.TryGetProperty("ts", out var tsEl) || tsEl.ValueKind != JsonValueKind.String) continue;
            var timestamp = AIUsageISO8601.Parse(tsEl.GetString()!);
            if (timestamp is not { } ts || ts < since) continue;

            var completion = (int)(ProviderParse.Number(GetOrNull(ctx, "completion_tokens")) ?? 0);
            var reasoning = (int)(ProviderParse.Number(GetOrNull(ctx, "reasoning_tokens")) ?? 0);
            var cached = Math.Min(ProviderParse.Number(GetOrNull(ctx, "cached_prompt_tokens")) ?? 0, promptTokens);
            var cacheRead = (int)cached;
            var inputNoCache = (int)Math.Max(0, promptTokens - cached);
            var output = completion + reasoning;

            var day = DailyUsageAccumulator.DayKey(ts);
            var totalTokens = (int)promptTokens + output;

            if (pid is not { } pidVal || !modelByPid.TryGetValue(pidVal, out var attributedModel)) continue;
            var tokenBreakdown = new TokenBreakdown { Input = inputNoCache, CacheRead = cacheRead, Output = output };
            var cost = pricing.EstimatedCostDollars(attributedModel, tokenBreakdown);
            if (cost is not { } c)
            {
                if (totalTokens > 0) accumulator.AddUnknownModel(day, attributedModel);
                continue;
            }
            accumulator.Add(day, totalTokens, c, attributedModel);
        }

        return accumulator.Build();
    }

    private static string? ModelId(string msg, JsonElement ctx)
    {
        JsonElement? raw = msg switch
        {
            "model changed" => GetOrNull(ctx, "model"),
            "model catalog: notifying clients" => GetOrNull(ctx, "current_model_id"),
            "backend_search: model switch" => GetOrNull(ctx, "model") ?? GetOrNull(ctx, "current_model_id") ?? GetOrNull(ctx, "model_id"),
            "subagent model resolved" => GetOrNull(ctx, "model_id") ?? GetOrNull(ctx, "model"),
            _ => null
        };
        if (raw is not { } r || r.ValueKind != JsonValueKind.String) return null;
        var model = r.GetString()?.Trim();
        return string.IsNullOrEmpty(model) ? null : model;
    }

    private static JsonElement? GetOrNull(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : null;
}
