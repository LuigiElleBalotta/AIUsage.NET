using System.Text.Json;
using AIUsage.Core.Models;
using AIUsage.Core.Services;

namespace AIUsage.Core.Stores;

/// <summary>
/// Mutable layout: which widgets are enabled, provider order, and each provider's metric order.
/// Simplified port of the Swift LayoutStore: keeps the core data model (placed widgets, provider/metric
/// order, pins, expanded-metric membership) and persistence, but omits UI-only concerns not yet needed
/// without a built UI — in-popover screen navigation, the undo stack, and the transient notice pills.
/// These can be added incrementally once the WPF UI is built and it's clear what it actually needs.
/// </summary>
public sealed class LayoutStore
{
    public const int MaxPinsPerProvider = 2;

    public WidgetRegistry Registry { get; }
    public List<PlacedWidget> Placed { get; private set; }
    public List<string> ProviderOrder { get; private set; }
    public Dictionary<string, List<string>> MetricOrderByProvider { get; private set; }
    public HashSet<string> PinnedMetricIds { get; private set; }
    public HashSet<string> ExpandedMetricIds { get; private set; }
    public HashSet<string> ExpandedProviderIds { get; private set; }
    public MenuBarStyle MenuBarStyle { get; set; }

    private readonly ISettingsStore _settings;
    private readonly string _storageKey;
    private readonly List<string> _defaultMetricIds;
    private readonly List<string> _defaultPinnedMetricIds;
    private readonly List<string> _defaultExpandedMetricIds;
    private readonly Func<string, bool> _isProviderEnabled;

    public LayoutStore(
        WidgetRegistry registry,
        ISettingsStore? settings = null,
        string storageKey = "aiusage.layout.v1",
        List<string>? defaultMetricIds = null,
        List<string>? defaultPinnedMetricIds = null,
        List<string>? defaultExpandedMetricIds = null,
        Func<string, bool>? isProviderEnabled = null)
    {
        Registry = registry;
        _settings = settings ?? FileSettingsStore.Shared;
        _storageKey = storageKey;
        _defaultMetricIds = defaultMetricIds ?? DefaultLayout.MetricIds;
        _defaultPinnedMetricIds = defaultPinnedMetricIds ?? DefaultLayout.PinnedMetricIds;
        _defaultExpandedMetricIds = defaultExpandedMetricIds ?? DefaultLayout.ExpandedMetricIds;
        _isProviderEnabled = isProviderEnabled ?? (_ => true);

        var savedPlaced = LoadPlaced();
        var hasStoredLayout = savedPlaced is not null;
        Placed = savedPlaced ?? _defaultMetricIds
            .Where(id => Registry.Descriptor(id) is not null)
            .Select(id => new PlacedWidget(id))
            .ToList();

        ProviderOrder = LoadProviderOrder() ?? Registry.Providers.Select(p => p.Id).ToList();
        MetricOrderByProvider = LoadMetricOrder() ?? DefaultMetricOrder();

        PinnedMetricIds = LoadStringSet($"{_storageKey}.menuBarPins")
            ?? new HashSet<string>(_defaultPinnedMetricIds.Where(id => Registry.Descriptor(id) is not null));

        if (LoadStringSet($"{_storageKey}.expandedMetrics") is { } savedExpanded)
        {
            ExpandedMetricIds = savedExpanded;
        }
        else if (hasStoredLayout)
        {
            ExpandedMetricIds = new HashSet<string>();
        }
        else
        {
            ExpandedMetricIds = new HashSet<string>(_defaultExpandedMetricIds.Where(id => Registry.Descriptor(id) is not null));
            PersistExpanded();
        }

        ExpandedProviderIds = LoadStringSet($"{_storageKey}.expandedProviders") ?? new HashSet<string>();
        MenuBarStyle = LoadMenuBarStyle();

        SyncPlacedOrder();
    }

    // MARK: - Provider expand/collapse

    public bool IsProviderExpanded(string providerId) => Registry.Provider(providerId) is not null && ExpandedProviderIds.Contains(providerId);

    public bool SetProviderExpanded(bool expanded, string providerId)
    {
        if (Registry.Provider(providerId) is null) return false;
        if (ExpandedProviderIds.Contains(providerId) == expanded) return false;
        if (expanded) ExpandedProviderIds.Add(providerId); else ExpandedProviderIds.Remove(providerId);
        PersistExpandedProviders();
        return true;
    }

    // MARK: - Enable / disable

    public void SetMetricEnabled(string descriptorId, bool enabled)
    {
        if (enabled)
        {
            Add(descriptorId);
        }
        else
        {
            var widget = Placed.FirstOrDefault(w => w.DescriptorId == descriptorId);
            if (widget is not null) Remove(widget.Id);
        }
    }

    public void Add(string descriptorId)
    {
        if (Registry.Descriptor(descriptorId) is null) return;
        if (Placed.Any(w => w.DescriptorId == descriptorId)) return;
        Placed.Add(new PlacedWidget(descriptorId));
        SyncPlacedOrder();
    }

    public void Remove(Guid id)
    {
        var index = Placed.FindIndex(w => w.Id == id);
        if (index < 0) return;
        Placed.RemoveAt(index);
        Persist();
    }

    // MARK: - Pins

    public bool IsPinned(string descriptorId) => Registry.Descriptor(descriptorId) is not null && PinnedMetricIds.Contains(descriptorId);

    public int PinnedCount(string providerId) => PinnedMetricIds.Count(id => Registry.Descriptor(id)?.ProviderId == providerId);

    public bool CanPin(string descriptorId)
    {
        if (PinnedMetricIds.Contains(descriptorId)) return true;
        var descriptor = Registry.Descriptor(descriptorId);
        if (descriptor is null || !descriptor.Pinnable) return false;
        return PinnedCount(descriptor.ProviderId) < MaxPinsPerProvider;
    }

    public void SetPinned(bool pinned, string descriptorId)
    {
        if (pinned)
        {
            if (!CanPin(descriptorId) || Registry.Descriptor(descriptorId) is null) return;
            if (!PinnedMetricIds.Add(descriptorId)) return;
        }
        else
        {
            if (!PinnedMetricIds.Remove(descriptorId)) return;
        }
        PersistPins();
    }

    public void TogglePin(string descriptorId) => SetPinned(!IsPinned(descriptorId), descriptorId);

    // MARK: - Reset

    public void ResetToDefault()
    {
        Placed = _defaultMetricIds.Where(id => Registry.Descriptor(id) is not null).Select(id => new PlacedWidget(id)).ToList();
        ProviderOrder = Registry.Providers.Select(p => p.Id).ToList();
        PersistProviderOrder();
        MetricOrderByProvider = DefaultMetricOrder();
        PersistMetricOrder();
        PinnedMetricIds = new HashSet<string>(_defaultPinnedMetricIds.Where(id => Registry.Descriptor(id) is not null));
        PersistPins();
        ExpandedMetricIds = new HashSet<string>(_defaultExpandedMetricIds.Where(id => Registry.Descriptor(id) is not null));
        PersistExpanded();
        ExpandedProviderIds = new HashSet<string>();
        PersistExpandedProviders();
        Persist();
    }

    // MARK: - Ordering

    public List<string> MetricOrder(string providerId)
    {
        var valid = Registry.DescriptorsFor(providerId).Select(d => d.Id).ToList();
        var saved = MetricOrderByProvider.GetValueOrDefault(providerId) ?? new List<string>();
        return NormalizedMetricIds(saved, valid);
    }

    private static List<string> NormalizedMetricIds(List<string> saved, List<string> validIds)
    {
        var validSet = new HashSet<string>(validIds);
        var seen = new HashSet<string>();
        var ordered = saved.Where(id => validSet.Contains(id) && seen.Add(id)).ToList();
        ordered.AddRange(validIds.Where(id => !seen.Contains(id)));
        return ordered;
    }

    public List<string> OrderedProviderIds() => Registry.OrderedProviderIds(ProviderOrder);

    // MARK: - Drag-reorder (direct port of LayoutStore+Customization.swift, simplified: no
    // always-shown/on-demand divider section since the WPF dashboard has no expand-caret equivalent
    // yet — every visible metric lives in one flat per-provider order).

    /// <summary>Reorder whole providers when <paramref name="dragged"/>'s header is dropped onto
    /// <paramref name="target"/>'s. Works on the currently enabled provider order; disabled providers
    /// keep their relative position. Returns whether the order actually changed.</summary>
    public bool ReorderProvider(string dragged, string target)
    {
        var shown = OrderedProviderIds().Where(_isProviderEnabled).ToList();
        var next = Reordered(shown, dragged, target);
        if (next is null) return false;

        // Reorder only the visible slots in the raw persisted sequence. Disabled providers keep their
        // exact positions while the visible ids move around them.
        var shownSet = new HashSet<string>(shown);
        var replacements = new Queue<string>(next);
        var rebuilt = new List<string>();
        foreach (var providerId in ProviderOrder)
        {
            if (shownSet.Contains(providerId))
            {
                if (replacements.Count > 0) rebuilt.Add(replacements.Dequeue());
            }
            else
            {
                rebuilt.Add(providerId);
            }
        }
        while (replacements.Count > 0) rebuilt.Add(replacements.Dequeue());
        foreach (var providerId in OrderedProviderIds().Where(id => !rebuilt.Contains(id))) rebuilt.Add(providerId);

        ProviderOrder = rebuilt;
        PersistProviderOrder();
        SyncPlacedOrder();
        return true;
    }

    /// <summary>Reorder metrics within one provider when <paramref name="dragged"/> is dropped onto
    /// <paramref name="target"/> (both descriptor ids of that provider). Operates on the provider's
    /// full metric order so disabled metrics keep their place too. Returns whether anything actually
    /// changed.</summary>
    public bool ReorderMetric(string dragged, string target, string providerId)
    {
        if (dragged == target) return false;
        var ordered = MetricOrder(providerId);
        if (!ordered.Contains(dragged) || !ordered.Contains(target)) return false;

        var next = Reordered(ordered, dragged, target);
        if (next is null) return false;

        MetricOrderByProvider[providerId] = next;
        PersistMetricOrder();
        SyncPlacedOrder();
        return true;
    }

    /// <summary>Pure reorder: remove <paramref name="dragged"/>, reinsert it adjacent to
    /// <paramref name="target"/> (after it when moving down, before it when moving up). Returns null
    /// when either id is missing or they're identical.</summary>
    public static List<string>? Reordered(List<string> ids, string dragged, string target)
    {
        if (dragged == target) return null;
        var from = ids.IndexOf(dragged);
        var to = ids.IndexOf(target);
        if (from < 0 || to < 0) return null;

        var next = new List<string>(ids);
        next.RemoveAt(from);
        var adjusted = next.IndexOf(target);
        if (adjusted < 0) return null;
        var insert = from < to ? adjusted + 1 : adjusted;
        next.Insert(Math.Min(insert, next.Count), dragged);
        return next;
    }

    /// <summary>Every currently visible (enabled provider) placed widget, in display order.</summary>
    public List<PlacedWidget> VisiblePlaced => Placed.Where(w => Registry.Descriptor(w.DescriptorId) is { } d && _isProviderEnabled(d.ProviderId)).ToList();

    public WidgetDescriptor? DescriptorFor(PlacedWidget widget) => Registry.Descriptor(widget.DescriptorId);

    public void SyncPlacedOrder(bool persistChanges = true)
    {
        var byDescriptor = new Dictionary<string, PlacedWidget>();
        foreach (var w in Placed) byDescriptor.TryAdd(w.DescriptorId, w);

        var ordered = new List<PlacedWidget>();
        foreach (var providerId in OrderedProviderIds())
        {
            foreach (var metricId in MetricOrder(providerId))
            {
                if (byDescriptor.TryGetValue(metricId, out var w)) ordered.Add(w);
            }
        }
        var orderedIds = new HashSet<Guid>(ordered.Select(w => w.Id));
        ordered.AddRange(Placed.Where(w => !orderedIds.Contains(w.Id)));
        Placed = ordered;
        if (persistChanges) Persist();
    }

    private Dictionary<string, List<string>> DefaultMetricOrder()
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var provider in Registry.Providers)
        {
            result[provider.Id] = Registry.DescriptorsFor(provider.Id).Select(d => d.Id).ToList();
        }
        return result;
    }

    // MARK: - Persistence

    public void Persist() => _settings.SetString(_storageKey, JsonSerializer.Serialize(Placed.Select(w => new PlacedWidgetDto(w.Id, w.DescriptorId))));
    public void PersistProviderOrder() => _settings.SetString($"{_storageKey}.providerOrder", JsonSerializer.Serialize(ProviderOrder));
    public void PersistMetricOrder() => _settings.SetString($"{_storageKey}.metricOrderByProvider", JsonSerializer.Serialize(MetricOrderByProvider));
    public void PersistPins() => _settings.SetString($"{_storageKey}.menuBarPins", JsonSerializer.Serialize(PinnedMetricIds));
    public void PersistExpanded() => _settings.SetString($"{_storageKey}.expandedMetrics", JsonSerializer.Serialize(ExpandedMetricIds));
    private void PersistExpandedProviders() => _settings.SetString($"{_storageKey}.expandedProviders", JsonSerializer.Serialize(ExpandedProviderIds));

    private sealed record PlacedWidgetDto(Guid Id, string DescriptorId);

    private List<PlacedWidget>? LoadPlaced()
    {
        var raw = _settings.GetString(_storageKey);
        if (raw is null) return null;
        try
        {
            var dtos = JsonSerializer.Deserialize<List<PlacedWidgetDto>>(raw);
            return dtos?.Select(d => new PlacedWidget(d.DescriptorId, d.Id)).ToList();
        }
        catch
        {
            return null;
        }
    }

    private List<string>? LoadProviderOrder()
    {
        var raw = _settings.GetString($"{_storageKey}.providerOrder");
        if (raw is null) return null;
        try { return JsonSerializer.Deserialize<List<string>>(raw); } catch { return null; }
    }

    private Dictionary<string, List<string>>? LoadMetricOrder()
    {
        var raw = _settings.GetString($"{_storageKey}.metricOrderByProvider");
        if (raw is null) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(raw); } catch { return null; }
    }

    private HashSet<string>? LoadStringSet(string key)
    {
        var raw = _settings.GetString(key);
        if (raw is null) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(raw);
            return list is not null ? new HashSet<string>(list) : null;
        }
        catch
        {
            return null;
        }
    }

    private MenuBarStyle LoadMenuBarStyle()
    {
        var raw = _settings.GetString($"{_storageKey}.menuBarStyle");
        return raw is not null && Enum.TryParse<MenuBarStyle>(raw, out var style) ? style : MenuBarStyle.Text;
    }
}
