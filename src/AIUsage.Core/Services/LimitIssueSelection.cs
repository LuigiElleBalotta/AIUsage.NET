using AIUsage.Core.Models;

namespace AIUsage.Core.Services;

/// <summary>One machine-facing provider issue for the limits envelope and CLI stderr warnings.</summary>
internal sealed record LimitIssue(string ProviderId, string Message);

/// <summary>
/// Merges the two issue sources the limits surface knows about: fatal refresh errors from
/// <see cref="Stores.WidgetDataStore.ProviderErrors"/> and the optional nonfatal issue a provider can
/// attach to a stored snapshot (for example OpenCode quota unavailable while local usage is fine).
/// A fatal error wins for the same provider, providers are never duplicated, and only the caller's
/// selected provider ids are considered.
/// </summary>
internal static class LimitIssueSelection
{
    public static IReadOnlyList<LimitIssue> Select(
        IReadOnlyList<string> providerIds,
        IReadOnlyDictionary<string, ProviderSnapshot> snapshots,
        IReadOnlyDictionary<string, string>? fatalErrors)
    {
        var issues = new List<LimitIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var providerId in providerIds)
        {
            if (!seen.Add(providerId)) continue;

            if (fatalErrors is not null
                && fatalErrors.TryGetValue(providerId, out var fatal)
                && !string.IsNullOrWhiteSpace(fatal))
            {
                issues.Add(new LimitIssue(providerId, fatal));
                continue;
            }

            if (snapshots.TryGetValue(providerId, out var snapshot)
                && !string.IsNullOrWhiteSpace(snapshot.LimitsError))
            {
                issues.Add(new LimitIssue(providerId, snapshot.LimitsError));
            }
        }

        return issues;
    }
}
