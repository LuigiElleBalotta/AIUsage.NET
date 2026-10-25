using AIUsage.Core.Models;

namespace AIUsage.Core.Providers.OpenCode;

/// <summary>
/// Provenance gate for OpenCode's persisted quota. Older builds inferred Session/Weekly/Monthly from
/// local spend with hardcoded 12/30/60 limits and wrote those <see cref="MetricLine.Progress"/> rows
/// (and a "Go" plan claim) into the shared snapshot cache. Since that cache lives across upgrades, a
/// recent-looking legacy entry must never be served as if it were the server-normalized Console
/// quota. <see cref="ProviderSnapshot.DataVersion"/> marks snapshots written by the current
/// semantics; missing or unrecognized markers are treated as incompatible legacy quota data.
///
/// Normalization is deliberately narrow: it removes only the OpenCode quota claim (the three meter
/// rows, the plan label, and the old quota status), keeps every local spend/history row and its
/// qualifiers, and adds one friendly status so the card and the local API do not silently present an
/// empty current quota. The sanitized snapshot keeps its stale timestamp and stays ineligible for a
/// cache hit, so the next read actually refreshes instead of serving it as current.
/// </summary>
public static class OpenCodeQuotaCompatibility
{
    /// <summary>Marker written by the current server-normalized Console quota refresh.</summary>
    public const string CurrentDataVersion = "opencode-console-quota-v1";

    private const string ProviderId = "opencode";
    private const string RefreshRequiredBadgeText = "Refresh Required";
    private const string RefreshRequiredBadgeColor = "#F59E0B";
    private const string RefreshRequiredBadgeSubtitle =
        "The quota from an earlier AIUsage.NET version was inferred locally and has been removed. " +
        "Refresh to check your Console quota again.";

    /// <summary>Machine-facing explanation attached to the sanitized snapshot (local API/CLI).</summary>
    public const string RefreshRequiredLimitsError =
        "OpenCode quota needs a fresh Console check after an upgrade.";

    private static readonly string[] LegacyQuotaLabels = { "Session", "Weekly", "Monthly" };

    /// <summary>True for non-OpenCode snapshots and for OpenCode snapshots carrying the current
    /// marker; false for missing or unrecognized OpenCode markers.</summary>
    public static bool IsCurrent(ProviderSnapshot snapshot)
    {
        if (snapshot.ProviderId != ProviderId) return true;
        return string.Equals(snapshot.DataVersion, CurrentDataVersion, StringComparison.Ordinal);
    }

    /// <summary>A snapshot may serve a fresh cache hit only when its quota semantics are current.</summary>
    public static bool IsEligibleForCacheHit(ProviderSnapshot snapshot) => IsCurrent(snapshot);

    /// <summary>
    /// Central normalization used by the snapshot cache on every decode and write. Idempotent: running
    /// it on its own output yields the same lines, plan, and limits error.
    /// </summary>
    public static ProviderSnapshot Normalize(ProviderSnapshot snapshot)
    {
        if (IsCurrent(snapshot)) return snapshot;

        var lines = snapshot.Lines
            .Where(line => !IsLegacyQuotaLine(line))
            .ToList();
        lines.Add(OpenCodeUsageMapper.QuotaBadge(
            RefreshRequiredBadgeText, RefreshRequiredBadgeColor, RefreshRequiredBadgeSubtitle));

        return snapshot with
        {
            // The old "Go" label was inferred from a local Go key/history, not reported by the
            // Console; it is part of the same incompatible quota claim.
            Plan = null,
            Lines = lines,
            LimitsError = RefreshRequiredLimitsError
        };
    }

    private static bool IsLegacyQuotaLine(MetricLine line) => line switch
    {
        MetricLine.Progress progress => LegacyQuotaLabels.Contains(progress.Label),
        MetricLine.Badge badge => badge.Label == OpenCodeUsageMapper.QuotaBadgeLabel,
        _ => false
    };
}
