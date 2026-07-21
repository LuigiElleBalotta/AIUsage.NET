using AIUsage.Core.Models;
using AIUsage.Core.Providers;

namespace AIUsage.Core.Stores;

/// <summary>
/// Read-only catalog of providers and the widgets they register, built from the live provider
/// runtimes at launch. Direct port of the Swift WidgetRegistry.
/// </summary>
public sealed class WidgetRegistry
{
    public List<Provider> Providers { get; }
    public List<WidgetDescriptor> Descriptors { get; }

    private readonly Dictionary<string, WidgetDescriptor> _descriptorsById;
    private readonly Dictionary<string, Provider> _providersById;
    private readonly Dictionary<string, List<WidgetDescriptor>> _descriptorsByProvider;

    public WidgetRegistry(List<Provider> providers, List<WidgetDescriptor> descriptors)
    {
        Providers = providers;
        Descriptors = descriptors;
        _descriptorsById = new Dictionary<string, WidgetDescriptor>();
        foreach (var d in descriptors) _descriptorsById.TryAdd(d.Id, d);
        _providersById = new Dictionary<string, Provider>();
        foreach (var p in providers) _providersById.TryAdd(p.Id, p);
        _descriptorsByProvider = new Dictionary<string, List<WidgetDescriptor>>();
        foreach (var d in descriptors)
        {
            if (!_descriptorsByProvider.TryGetValue(d.ProviderId, out var list))
            {
                list = new List<WidgetDescriptor>();
                _descriptorsByProvider[d.ProviderId] = list;
            }
            list.Add(d);
        }
    }

    public WidgetDescriptor? Descriptor(string id) => _descriptorsById.GetValueOrDefault(id);
    public Provider? Provider(string id) => _providersById.GetValueOrDefault(id);
    public List<WidgetDescriptor> DescriptorsFor(string providerId) => _descriptorsByProvider.GetValueOrDefault(providerId) ?? new();

    /// <summary>Saved provider order filtered to installed providers, with newly introduced providers appended.
    /// (Account-card slotting from the Swift edition is omitted — no multi-account support yet.)</summary>
    public List<string> OrderedProviderIds(List<string> savedOrder)
    {
        var defaultIds = Providers.Select(p => p.Id).ToList();
        var known = new HashSet<string>(defaultIds);
        var saved = savedOrder.Where(known.Contains).ToList();
        var savedIds = new HashSet<string>(saved);
        var result = new List<string>(saved);
        foreach (var id in defaultIds.Where(id => !savedIds.Contains(id))) result.Add(id);
        return result;
    }

    public Dictionary<string, List<WidgetDescriptor>> LimitDescriptorsByProvider =>
        _descriptorsByProvider.ToDictionary(kv => kv.Key, kv => kv.Value.Where(d => (d.LimitResources?.Count ?? 0) > 0).ToList());

    public Dictionary<string, UsageHistoryDescriptor> HistoryDescriptorsByProvider =>
        _descriptorsByProvider
            .Select(kv => (kv.Key, History: kv.Value.Select(d => d.HistoryResource).FirstOrDefault(h => h is not null)))
            .Where(t => t.History is not null)
            .ToDictionary(t => t.Key, t => t.History!);

    public static WidgetRegistry FromProviders(List<IProviderRuntime> runtimes)
    {
        var providers = runtimes.Select(r => r.Provider).ToList();
        var descriptors = runtimes.SelectMany(r => r.WidgetDescriptors).ToList();
        return new WidgetRegistry(providers, descriptors);
    }
}
