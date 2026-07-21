using AIUsage.Core.Providers;
using AIUsage.Core.Stores;
using AIUsage.Core.Support;

namespace AIUsage.Core.App;

/// <summary>
/// Turns on providers that arrived with an update — but only the ones the user actually has. Direct
/// port of the Swift NewProviderSeeder.
///
/// <see cref="FirstRunSeeder"/> handles the very first launch; this is its every-later-launch
/// sibling. It diffs the registry against the store's known-provider set: anything never seen before
/// gets the same local-only <see cref="IProviderRuntime.HasLocalCredentialsAsync"/> probe as first-run
/// detection, and is enabled on a hit. Providers the install has already seen are never touched, so a
/// user's choice to keep one off is never overridden.
///
/// One-shot semantics: new IDs are marked known synchronously, before the probe. A new provider
/// without credentials stays off and is never probed again — enabling it later is the user's call.
/// </summary>
public static class NewProviderSeeder
{
    /// <summary>Returns the detection task (for tests/callers to await), or null when there is
    /// nothing to do — the common case: no new providers, or a store still in legacy all-on mode
    /// (where new providers default to on already, so there is nothing to detect).</summary>
    public static Task? ReconcileIfNeeded(List<IProviderRuntime> providers, ProviderEnablementStore enablement)
    {
        if (enablement.EnabledIds is null) return null;

        var currentIds = new HashSet<string>(providers.Select(p => p.Provider.Id));

        // An enabled-list store with no known set predates the tracking (an unbundled dev build seeded
        // before this shipped). Baseline it to the current registry without probing: we can't tell
        // "new" from "user turned it off", so auto-enabling anything here could override a real choice.
        if (enablement.KnownIds.Count == 0)
        {
            enablement.RegisterKnownProviders(currentIds);
            return null;
        }

        var newIds = enablement.RegisterKnownProviders(currentIds);
        if (newIds.Count == 0) return null;

        AppLog.Info(LogTag.Config, $"new providers since last run: {string.Join(", ", newIds.OrderBy(x => x, StringComparer.Ordinal))}; probing local credentials");

        return Task.Run(async () =>
        {
            var newProviders = providers.Where(p => newIds.Contains(p.Provider.Id)).ToList();
            var detected = await FirstRunSeeder.DetectLocalProviders(newProviders).ConfigureAwait(false);
            foreach (var id in detected.OrderBy(x => x, StringComparer.Ordinal))
            {
                // The probe takes a moment; if the user already turned the provider on themselves,
                // leave their toggle alone (SetEnabled would be a no-op anyway).
                if (enablement.IsEnabled(id)) continue;
                AppLog.Info(LogTag.Config, $"new provider {id}: credentials detected, enabling");
                enablement.SetEnabled(true, id);
            }
        });
    }
}
