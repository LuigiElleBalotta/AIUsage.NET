namespace AIUsage.Core.Models;

/// <summary>One external quick-link button on a provider card.</summary>
public sealed record ProviderLink(string Label, string Url);

/// <summary>A data source (Claude, Codex, Cursor, ...) that registers widgets it knows how to feed.</summary>
public sealed class Provider
{
    public string Id { get; }
    public string DisplayName { get; }
    public string IconKey { get; }
    public IReadOnlyList<ProviderLink> Links { get; }

    public Provider(string id, string displayName, string iconKey, IReadOnlyList<ProviderLink>? links = null)
    {
        Id = id;
        DisplayName = displayName;
        IconKey = iconKey;
        Links = links ?? Array.Empty<ProviderLink>();
    }

    public IEnumerable<ProviderLink> VisibleLinks => Links.Where(l =>
        !string.IsNullOrWhiteSpace(l.Label) &&
        !string.IsNullOrWhiteSpace(l.Url) &&
        (l.Url.StartsWith("https://", StringComparison.Ordinal) || l.Url.StartsWith("http://", StringComparison.Ordinal)));

    public override bool Equals(object? obj) => obj is Provider p && p.Id == Id;
    public override int GetHashCode() => Id.GetHashCode();
}
