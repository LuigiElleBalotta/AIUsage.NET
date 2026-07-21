using AIUsage.Core.Models;
using AIUsage.Core.Providers;
using AIUsage.Core.Services;
using AIUsage.Core.Stores;
using AIUsage.Core.Support;

namespace AIUsage.Core.App;

/// <summary>
/// Composition root: owns the (constant) registry and the (mutable) stores. Simplified port of the
/// Swift AppContainer — keeps provider catalog wiring, enablement, layout, data store, first-run
/// seeding, the periodic refresh loop, and the local HTTP API. Omits (not yet ported, see
/// PORTING_NOTES.md): multi-account Claude cards, iCloud sync, quota-pace notifications, telemetry,
/// the resets-claim service, and the login-shell environment capture (unnecessary on Windows — a
/// normal process already inherits persisted user/machine env vars).
/// </summary>
public sealed class AppContainer : IDisposable
{
    public WidgetRegistry Registry { get; }
    public LayoutStore Layout { get; }
    public WidgetDataStore DataStore { get; }
    public ProviderEnablementStore Enablement { get; }
    public List<IApiKeyManaging> ApiKeyProviders { get; }

    private readonly List<IProviderRuntime> _providers;
    private readonly CancellationTokenSource _refreshLoopCts = new();
    private readonly Task _refreshLoopTask;
    private readonly Task? _seedTask;
    private readonly LocalUsageServer _localServer;

    /// <summary>Raised after every refresh pass (batch or single-provider), so a UI layer can repaint.</summary>
    public event Action? SnapshotsChanged;

    public AppContainer(bool isFreshInstall = false)
    {
        _providers = ProviderCatalog.Make();
        Registry = WidgetRegistry.FromProviders(_providers);
        ApiKeyProviders = _providers.OfType<IApiKeyManaging>().ToList();

        Enablement = new ProviderEnablementStore();
        Layout = new LayoutStore(Registry, isProviderEnabled: Enablement.IsEnabled);
        DataStore = new WidgetDataStore(Registry, _providers, isProviderEnabled: Enablement.IsEnabled);

        Enablement.ProviderEnabled += id => DataStore.ClearFailureBackoff(id);
        Enablement.Changed += () => DataStore.ProviderEnablementDidChange();

        _seedTask = FirstRunSeeder.SeedIfNeeded(isFreshInstall, _providers, Enablement)
            ?? NewProviderSeeder.ReconcileIfNeeded(_providers, Enablement);

        _refreshLoopTask = StartPeriodicRefresh(_refreshLoopCts.Token);

        _localServer = new LocalUsageServer(BuildLocalApiState);
        _localServer.Start();
    }

    /// <summary>Snapshots the current registry/data-store state into the immutable shape the local
    /// HTTP API and CLI both read through. Captured fresh on every request — the API always serves
    /// whatever the app is currently showing.</summary>
    private LocalUsageApi.State BuildLocalApiState()
    {
        var knownIds = new HashSet<string>(Registry.Providers.Select(p => p.Id));
        var enabledOrderedIds = Layout.OrderedProviderIds().Where(Enablement.IsEnabled).ToList();
        return new LocalUsageApi.State(
            EnabledOrderedIds: enabledOrderedIds,
            KnownIds: knownIds,
            Snapshots: DataStore.Snapshots,
            LimitDescriptors: Registry.LimitDescriptorsByProvider,
            Errors: DataStore.ProviderErrors);
    }

    /// <summary>Re-runs first-launch credential detection on demand (Customize "Reset All").</summary>
    public Task ReseedEnabledProviders() => FirstRunSeeder.Reseed(_providers, Enablement);

    private async Task StartPeriodicRefresh(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await DataStore.RefreshAllAsync(cancellationToken: token).ConfigureAwait(false);
                SnapshotsChanged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error(LogTag.Lifecycle, $"refresh loop iteration failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(RefreshSetting.Interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Forces an immediate out-of-band refresh of one provider (e.g. a manual tray refresh click).</summary>
    public async Task<RefreshOutcome> RefreshNowAsync(string providerId)
    {
        var outcome = await DataStore.RefreshAsync(providerId, force: true).ConfigureAwait(false);
        SnapshotsChanged?.Invoke();
        return outcome;
    }

    /// <summary>Forces an immediate out-of-band refresh of every enabled provider.</summary>
    public async Task RefreshAllNowAsync()
    {
        await DataStore.RefreshAllAsync(force: true).ConfigureAwait(false);
        SnapshotsChanged?.Invoke();
    }

    public void Dispose()
    {
        _localServer.Dispose();
        _refreshLoopCts.Cancel();
        try { _refreshLoopTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort shutdown */ }
        _refreshLoopCts.Dispose();
    }
}
