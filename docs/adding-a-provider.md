# Adding a Provider

How to add a new AI provider to AIUsage.NET. Read the [architecture overview](architecture.md) first
so the pieces below make sense.

## What a provider is

A provider is a small C# module under `src/AIUsage.Core/Providers/<Name>/` that implements
`IProviderRuntime`. It has three parts:

- an **auth store** that reads credentials already on the user's machine (config files, Windows
  Credential Manager, SQLite state databases),
- a **usage client** that calls the provider's API,
- a **mapper** that turns the response into the app's metric vocabulary.

AIUsage.NET never asks the user to paste a token — if the provider's own CLI or app has already
logged in, AIUsage.NET reads those existing credentials. (OpenRouter and Z.ai are the two
exceptions, since neither has a local companion tool to read from — see `IApiKeyManaging` below.)

Besides `RefreshAsync()`, every provider implements `HasLocalCredentialsAsync()` — a cheap,
local-only check (files, Windows Credential Manager; never the network) for whether those
credentials exist at all. `FirstRunSeeder` probes it once on a fresh install to turn on exactly the
providers the user actually has. Mirror the same credential sources `RefreshAsync()` reads.

## The metric contract

`RefreshAsync()` returns a `ProviderSnapshot` whose `Lines` are `MetricLine` values (a closed
record hierarchy under `AIUsage.Core/Models/MetricLine.cs`). Pick the case by the shape of the
number, not by the provider:

- **`MetricLine.Progress`** — a bounded meter with `Used`, `Limit`, and a `Format`:
  - `ProgressFormat.Percent` for quota-style limits (session, weekly),
  - `ProgressFormat.Dollars` for a capped dollar amount,
  - `ProgressFormat.Count(suffix)` for a capped count.
  - Add `ResetsAt` when the window resets at a known time, and `PeriodDurationMs` for the cycle
    length.
- **`MetricLine.Values`** — an unbounded row carrying one or more raw numbers (each a
  `MetricValue`: a number, its `MetricKind`, an optional unit label like `"tokens"`). Use it for any
  limitless numeric row.
- **`MetricLine.Badge`** — a short status pill, like a pay-as-you-go cap or an error message
  (`MetricLine.ErrorBadgeLabel` marks a line as an error for `WidgetDataStore`'s error detection).
- **`MetricLine.Chart`** — dated numeric points for a usage-trend row (`MetricChartPoint`).
- **`MetricLine.Text`** — a plain string notice. It does not resolve to a `WidgetData` in
  `WidgetDataStore.Resolve` (returns `null`), so avoid it for anything that should render.

Set the snapshot's `Plan` when the provider exposes a plan name. On failure, return
`ProviderSnapshot.Error(provider, exception)` or `ProviderSnapshot.ErrorWithMessage(...)` — never
return stale or empty data silently; a caught exception should always become a visible error line.

## Steps

1. **Create the module.** Add `src/AIUsage.Core/Providers/<Name>/` with the auth store, usage
   client, and mapper, implementing `IProviderRuntime` — both `RefreshAsync()` and
   `HasLocalCredentialsAsync()`. The probe must stay local-only and reuse the same auth-store
   loaders and credential-usability filters that `RefreshAsync()` starts with — don't write a
   second credential-reading path. Reuse the shared helpers in `Support/` (`ProviderParse` for
   JSON/number/percent parsing, `AIUsageISO8601` for timestamps) instead of copying them.
2. **Declare its widgets.** Expose the provider's metrics as `WidgetDescriptor`s using the factories
   in `WidgetDescriptorFactories` (`Percent`, `BoundedDollars`, `BoundedCount`, `Values`, `Combined`,
   `Badge`, `SpendTiles`, `UsageTrend`, `DollarBalance`).
3. **Register it.** Add the provider to the list in `ProviderCatalog.Make()`
   (`src/AIUsage.Core/Providers/ProviderCatalog.cs`) — Claude, Codex, Cursor first, then
   alphabetically.
4. **Test it.** There is no test project yet in this port (see [PORTING_NOTES.md](../PORTING_NOTES.md))
   — adding one, with a focused mapper test (feed a sample API response, check the resulting metric
   lines), is a good opportunity if you're adding a provider.
5. **Document it.** Add a page under `docs/providers/` covering what it tracks, where its
   credentials come from, the endpoints it calls, and what its error states mean — follow the shape
   of the existing provider docs.
6. **Run it.** Build and launch with `script/build_and_run.ps1` (or `-Mode cli <name> --force` to
   check it via the CLI without needing the tray UI) and confirm the provider shows real data.

## Conventions

- Validate only at the boundary (the API response); trust the app's internal types.
- Match the metric labels and units the provider's own dashboard uses, so numbers are recognizable.
- Declare the provider's quick links on its `Provider` value (`Links:`), via `ProviderLink(label,
  url)`. Cap at two links per provider. There is currently no UI that renders these as buttons (see
  [dashboard.md](dashboard.md)) — they're carried on the model for when that UI exists.

## User-supplied API keys

A provider with nothing local to read (OpenRouter, Z.ai) implements `IApiKeyManaging` in addition to
`IProviderRuntime`:

- The auth store exposes `ApiKeyStatus`, `CurrentApiKey()`, `SaveApiKey(key)`, and `DeleteApiKey()`
  — reading/writing a config file under `~/.aiusage/<provider>.json`, with an environment-variable
  fallback.
- `AppContainer` collects every `IApiKeyManaging` provider into `ApiKeyProviders`. There is no
  Settings UI yet to actually manage keys through (see [dashboard.md](dashboard.md)) — for now, keys
  must be set via the config file or environment variable directly.

Persist the key to a file the auth store already checks (don't introduce a parallel store), so the
file remains the source of truth and a user can still edit it by hand.
