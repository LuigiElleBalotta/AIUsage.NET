# Refreshing & Caching

## When data updates

- All enabled providers refresh together: once at `AppContainer` construction (tray app startup),
  then every 5 minutes on a fixed cadence (`RefreshSetting.Interval`). There is no setting to change
  this yet. Providers fetch concurrently (`Task.WhenAll`), so a slow provider doesn't hold up the
  others; the batch as a whole finishes only once every provider has returned.
- Turning a provider on fires `ProviderEnablementStore.ProviderEnabled`, which clears that provider's
  failure backoff so it's eligible to fetch promptly rather than waiting out the full interval — but
  there is currently no UI to turn a provider on/off, so this only matters if you call
  `ProviderEnablementStore.SetEnabled` directly (e.g. from a future Customize screen).
- **Refresh Now** in the tray icon's right-click menu (and the equivalent `AppContainer.RefreshAllNowAsync()`
  call) forces an immediate refresh of every enabled provider, bypassing the cache.
- `aiusage` reuses the same persisted, five-minute cache; `aiusage --force` forces a refresh the same
  way Refresh Now does.

## Caching

Snapshots are cached in `%LOCALAPPDATA%\AIUsage\settings.json` (`ProviderSnapshotCache`) and loaded
at `WidgetDataStore` construction, so the tray app shows last-known values immediately at launch
instead of a blank window — even before the first fetch finishes (stale-while-revalidate).

A cached value only counts as *fresh* (skip-a-refresh fresh) when it was written **during the
current running process** (`ProviderSnapshotCache`'s `allowsPersistedFreshness` defaults to `false`
for the tray app's cache instance). So a value cached in an earlier run always re-fetches on the
first pass after a restart — you still see it instantly from disk, but the app never waits out the
old interval before getting live numbers. `aiusage`'s `UsageReader` constructs its
`ProviderSnapshotCache` with `allowsPersistedFreshness: true` instead, since a one-shot CLI process
has no "current session" to compare against — it trusts the persisted cache's age directly.

There is no separate local-log parse cache on disk (unlike the original's
`~/Library/Application Support/OpenUsage/log-scan-cache/`): `IncrementalJsonlScanner<T>` keeps only
an in-memory index of scanned files (path/size/mtime) that resets every process restart. See
[architecture.md](architecture.md) and PORTING_NOTES.md.

## When a fetch fails

A failed refresh **never wipes your data**: the last good `ProviderSnapshot` stays in
`WidgetDataStore.Snapshots`, and the failure is recorded in `WidgetDataStore.ProviderErrors` (keyed
by provider ID) along with a 60-second retry backoff (`FailureRetryBackoff`) so a persistently
failing provider doesn't retry every tick. The tray UI does not currently surface these errors
visually (no warning-triangle equivalent yet) — a metric row simply keeps showing its last good
value. The `aiusage` CLI does surface them: a failed provider's error message appears in the process's
stderr warnings and the process exits with code `4`.

Rows that have never had data show `—` (the "no data" headline) rather than made-up numbers.

## Stale data

The underlying staleness computation exists (`WidgetDataStore.StalenessHint`, based on
`RefreshSetting.DefaultMinutes * 2`) but nothing in the tray UI displays it yet — see
[dashboard.md](dashboard.md#what-is-not-implemented-yet).
