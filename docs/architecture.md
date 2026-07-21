# Architecture

A high-level map of how AIUsage.NET is put together, for people working on the code. For what the app
*does*, start with the [behavior docs](README.md). For the full account of what diverges from the
original Swift/macOS edition and why, see [PORTING_NOTES.md](../PORTING_NOTES.md).

## The shape of the app

AIUsage.NET is a .NET 8 solution with a shared library and two thin executables:

- `src/AIUsage.Core` — the shared library: models, providers, pricing, stores, services, support
  helpers. Everything that isn't UI or a process entry point lives here.
- `src/AIUsage.Tray` — the WPF tray executable (`AIUsage.exe`): a `NotifyIcon` (WinForms — WPF has no
  native tray-icon API) plus a borderless popup window.
- `src/AIUsage.Cli` — the console executable (`aiusage.exe`): a one-shot reader of the same shared
  cache.

Within `AIUsage.Core`, code is grouped by role, mirroring the original's layout:

- `App/` — the composition root (`AppContainer`, `FirstRunSeeder`).
- `Models/` — the small value types the rest of the app speaks in (`MetricLine`, `WidgetData`,
  descriptors).
- `Providers/` — one folder per provider (Claude, Codex, Cursor, Devin, Grok, OpenCode, …).
- `Stores/` — the mutable state the tray app and CLI read (`WidgetDataStore`, `LayoutStore`,
  `ProviderEnablementStore`, `ProviderSnapshotCache`).
- `Services/` — shared infrastructure (HTTP client, credential access, SQLite access, settings
  file, proxy config, language-server discovery).
- `Support/` — small shared helpers (formatting, parsing, logging, ISO8601, pacing math).
- `Pricing/` — the shared model-pricing engine and its bundled/live data sources.

There is currently no `Views/` layer of comparable richness — the tray UI (`MetricsWindow`) is a
single, minimal code-behind file, not a set of composable views. See
[dashboard.md](dashboard.md) for what it actually renders today.

## Composition root

`AppContainer` (`AIUsage.Core/App/AppContainer.cs`) is the one place that wires everything together.
At construction it builds the list of providers (`ProviderCatalog.Make()`), turns it into a
`WidgetRegistry`, creates `ProviderEnablementStore`, `LayoutStore`, and `WidgetDataStore`, runs
first-run credential detection (`FirstRunSeeder`), and starts a periodic refresh loop (every 5
minutes, cancellable). `AIUsage.Tray`'s `App.xaml.cs` owns one `AppContainer` for the process
lifetime and hands it to `TrayController`, which owns the tray icon and the popup window.

`aiusage` (the CLI) does **not** go through `AppContainer` — it uses the smaller `UsageReader`
(`AIUsage.Core/Services/UsageReader.cs`), which constructs its own `ProviderCatalog` and
`WidgetRegistry` for the duration of one read, and reads/writes the same on-disk
`ProviderSnapshotCache` file the tray app uses. This keeps the CLI's exit-and-done contract simple
without needing to start (and shut down) a refresh loop for a single read. A normal invocation reads
the cache and exits; `--force` runs a real refresh through `WidgetDataStore` first.

Neither the CLI nor the tray app duplicates provider, auth, pricing, or mapping logic — both read
through the same `ProviderRuntime` implementations and the same `Pricing/` engine.

## The provider pipeline

Each provider is a small module that implements `IProviderRuntime`. A refresh flows through three
parts:

1. **Auth store** — reads credentials that already exist on the machine (config files, Windows
   Credential Manager, SQLite state databases). AIUsage.NET never asks the user to paste a token
   (except OpenRouter and Z.ai, which have no local credential to reuse — see their provider docs).
2. **Usage client** — makes the HTTP calls to the provider's API.
3. **Mapper** — turns the provider's response into the app's own vocabulary: a `ProviderSnapshot`
   containing typed `MetricLine` values (`Progress`, `Values`, `Badge`, `Chart`) plus `Text` notices.

Because every provider produces the same normalized `MetricLine` shapes, the tray UI and the CLI
render/serialize them all the same way and don't need to know provider-specific details. To add one,
see [Adding a provider](adding-a-provider.md).

Claude, Codex, and Grok share `IncrementalJsonlScanner<T>` (`Providers/JsonlScanning.cs`) for local
JSONL/log history. Unlike the original's disk-persisted, versioned cache, this port keeps **only an
in-memory cache** (path + size + mtime) — see PORTING_NOTES.md. This means the first refresh after
every app restart re-scans all local log files instead of reusing a prior session's parse results;
subsequent refreshes within the same run are still incremental.

## Stores

The tray app and CLI read from a few stateful stores:

- `WidgetDataStore` — the latest `ProviderSnapshot` per provider, plus the refresh/backoff machinery
  and the `MetricLine` → `WidgetData` resolution every rendering surface reads through.
- `LayoutStore` — which metrics are enabled, the provider/metric order, and which metrics are pinned.
  The undo stack, in-popover screen navigation, and transient notice pills from the original are not
  ported (no UI surface needs them yet).
- `ProviderEnablementStore` — which providers the user has turned on or off, backed by a JSON file
  and plain C# events (instead of `NotificationCenter`) to wake the refresh loop early.
- `ProviderSnapshotCache` — the persisted, TTL-gated cache shared by the tray app and the CLI.

Refresh runs on a timer inside `AppContainer`; each pass respects the cache and a per-provider
failure backoff, so the network is only hit once a snapshot has actually expired.

There is no `ICloudUsageSyncStore` equivalent (no cross-machine sync is ported) and no
peer-history aggregation — each machine's spend tiles reflect only that machine's own logs.

## The WPF/tray bridge

Windows has no direct equivalent of `NSStatusItem` + a key-capable `NSPanel` in WPF, so
`AIUsage.Tray` uses the standard pattern for a tray app on this platform: a
`System.Windows.Forms.NotifyIcon` (which requires enabling `UseWindowsForms` alongside `UseWPF` in
the project file) for the tray icon and context menu, and a plain borderless `Window`
(`MetricsWindow`, `WindowStyle="None"`, `Topmost="True"`) that closes when it loses focus
(`Deactivated`) as a simple approximation of "click outside to dismiss." There is no custom
non-activating panel, no height animation, and no vibrancy/transparency styling — see
[dashboard.md](dashboard.md) for exactly what the window renders today.

Because `System.Windows.Forms`, `System.Drawing`, and `System.Windows` (WPF) are all referenced in
the same project, several common type names collide (`Application`, `Brush`, `Brushes`,
`Orientation`) — resolved with explicit `using` aliases where needed.

## Local HTTP API

Not ported. The original's loopback JSON server on `127.0.0.1:6736` has no counterpart here; the CLI
reads/writes the shared snapshot cache directly instead of going through an HTTP layer. See
[PORTING_NOTES.md](../PORTING_NOTES.md) for the reasoning and what would need to change if this is
added later.
