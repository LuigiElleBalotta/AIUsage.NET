using AIUsage.Core.Providers;
using AIUsage.Core.Stores;
using AIUsage.Core.Support;

namespace AIUsage.Core.App;

/// <summary>
/// Seeds a fresh install's enabled providers so the first launch shows only the tools the user
/// actually has, instead of every provider AIUsage knows about. Direct port of the Swift
/// FirstRunSeeder. Two steps, first launch only (existing installs keep the all-on legacy default):
/// 1. Synchronously switch <see cref="ProviderEnablementStore"/> into enabled-list mode with the
///    established fallback set (Claude, Codex, Cursor).
/// 2. Asynchronously probe every provider's local-only credential check and replace the fallback with
///    exactly the detected set (unless nothing was detected, or the user already touched the toggles).
/// </summary>
public static class FirstRunSeeder
{
    public static readonly HashSet<string> FallbackProviderIds = new() { "claude", "codex", "cursor" };

    /// <summary>Returns the detection task (callers may await it), or null if no seeding happened.
    /// Idempotent: an already-seeded store is never overwritten.</summary>
    public static Task? SeedIfNeeded(
        bool isFreshInstall,
        List<IProviderRuntime> providers,
        ProviderEnablementStore enablement)
    {
        if (!isFreshInstall || enablement.EnabledIds is not null) return null;

        enablement.RegisterKnownProviders(providers.Select(p => p.Provider.Id));
        return SeedFallbackThenDetect(providers, enablement, "first run");
    }

    /// <summary>Re-runs first-launch detection on demand (Customize "Reset All"). Overwrites the current
    /// on/off choices with the fallback synchronously, then replaces it with the detected set.</summary>
    public static Task Reseed(List<IProviderRuntime> providers, ProviderEnablementStore enablement)
    {
        return SeedFallbackThenDetect(providers, enablement, "reset all", "re-probing");
    }

    private static Task SeedFallbackThenDetect(
        List<IProviderRuntime> providers,
        ProviderEnablementStore enablement,
        string logPrefix,
        string probeVerb = "probing")
    {
        var fallback = new HashSet<string>(FallbackProviderIds.Intersect(providers.Select(p => p.Provider.Id)));
        enablement.SeedEnabledProviders(fallback);
        AppLog.Info(LogTag.Config, $"{logPrefix}: seeded providers {string.Join(", ", fallback.OrderBy(x => x, StringComparer.Ordinal))}; {probeVerb} local credentials");

        return Task.Run(async () =>
        {
            var detected = await DetectLocalProviders(providers).ConfigureAwait(false);
            AppLog.Info(LogTag.Config, $"{logPrefix}: detected credentials for {string.Join(", ", detected.OrderBy(x => x, StringComparer.Ordinal))}");
            var current = enablement.EnabledIds;
            if (current is null || !current.SetEquals(fallback) || detected.Count == 0) return;
            enablement.SeedEnabledProviders(detected);
        });
    }

    /// <summary>Local-only credential probe across every provider, run concurrently.</summary>
    public static async Task<HashSet<string>> DetectLocalProviders(List<IProviderRuntime> providers)
    {
        var probes = providers.Select(p => (p.Provider.Id, Task: p.HasLocalCredentialsAsync())).ToList();
        var detected = new HashSet<string>();
        foreach (var (id, task) in probes)
        {
            if (await task.ConfigureAwait(false)) detected.Add(id);
        }
        return detected;
    }
}
