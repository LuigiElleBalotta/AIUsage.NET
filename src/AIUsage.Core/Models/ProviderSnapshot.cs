namespace AIUsage.Core.Models;

/// <summary>Latest normalized output for one provider refresh.</summary>
public sealed record ProviderSnapshot(
    string ProviderId,
    string DisplayName,
    string? Plan,
    IReadOnlyList<MetricLine> Lines,
    DateTimeOffset RefreshedAt,
    ProviderUsageHistory? UsageHistory = null,
    string? Warning = null,
    ErrorCategory? ErrorCategoryValue = null
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
