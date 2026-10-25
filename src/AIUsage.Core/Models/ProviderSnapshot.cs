namespace AIUsage.Core.Models;

/// <summary>
/// Latest normalized output for one provider refresh. <see cref="LimitsError"/> is an optional
/// nonfatal provider issue (such as quota unavailable while local usage is valid) that feeds the
/// machine-facing limits errors without turning the snapshot into a fatal error.
///
/// <see cref="DataVersion"/> is optional provenance for providers whose persisted snapshot semantics
/// changed incompatibly (currently OpenCode's server-normalized Console quota). It is internal cache
/// metadata: the public local API wire shape does not expose it. A provider that relies on it owns
/// the current marker and the compatibility rules; snapshots without a recognized marker keep their
/// local data but are not treated as current authoritative data.
/// </summary>
public sealed record ProviderSnapshot(
    string ProviderId,
    string DisplayName,
    string? Plan,
    IReadOnlyList<MetricLine> Lines,
    DateTimeOffset RefreshedAt,
    ProviderUsageHistory? UsageHistory = null,
    string? Warning = null,
    ErrorCategory? ErrorCategoryValue = null,
    string? LimitsError = null,
    string? DataVersion = null
)
{
    public MetricLine? Line(string label) => Lines.FirstOrDefault(l => l.Label == label);

    public static ProviderSnapshot Make(
        Provider provider,
        string? plan,
        IReadOnlyList<MetricLine> lines,
        DateTimeOffset refreshedAt,
        ProviderUsageHistory? usageHistory = null,
        string? warning = null)
    {
        return new ProviderSnapshot(provider.Id, provider.DisplayName, plan, lines, refreshedAt, usageHistory, warning);
    }

    public static ProviderSnapshot Error(Provider provider, Exception error, DateTimeOffset? now = null)
    {
        var category = (error as ICategorizedError)?.ErrorCategory ?? ErrorCategory.Other;
        return ErrorWithMessage(provider, error.Message, category, now);
    }

    public static ProviderSnapshot ErrorWithMessage(Provider provider, string message, ErrorCategory? category = null, DateTimeOffset? now = null)
    {
        return new ProviderSnapshot(
            provider.Id,
            provider.DisplayName,
            null,
            new List<MetricLine> { new MetricLine.Badge(MetricLine.ErrorBadgeLabel, message, "#EF4444") },
            now ?? DateTimeOffset.UtcNow,
            null,
            null,
            category);
    }
}
