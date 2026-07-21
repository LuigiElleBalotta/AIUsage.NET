# Dashboard

The popup window that opens from the tray icon (`AIUsage.exe`). This is closer to the original's
SwiftUI dashboard than earlier in the port (a real dark theme, real progress bars, provider brand
icons, a working Settings window), but is still simpler overall — see
[PORTING_NOTES.md](../PORTING_NOTES.md) for the full account of what's ported, adapted, or missing.

## What it shows today

Left-click the tray icon to open the window. It lists every enabled metric, grouped by provider
card, in the order `LayoutStore` reports (providers in `ProviderCatalog` order — Claude, Codex,
Cursor, then the rest alphabetically; metrics in each provider's `WidgetDescriptors` declaration
order). Each provider card has:

- A brand-colored icon badge and the provider's display name.
- A plan-name pill (e.g. "Free", "Pro 20x") when the provider reports one.
- One row per enabled metric: title on the left, headline value on the right (e.g. `48% used`,
  `$4.08 spent`), colored by severity — blue/default for normal, yellow for warning, red for
  critical, gray for "no data yet."
- A real progress bar under bounded metrics (anything with a limit), filled in proportion to
  `Fraction`/`RemainingFraction` and colored the same as the headline.
- A subtitle line (reset countdown, unit suffix, or dollar limit) under most rows.
- Usage Trend rows (`MetricLine.Chart`) render as a small bar chart, one thin bar per day sized
  against the series max, each with a hover tooltip showing that day's pre-formatted value and a
  source note underneath.
- Spend rows with a period-scoped model breakdown (Today/Yesterday/Last 30 Days, where the provider
  supplies per-model token/cost data) show a hover tooltip listing each model's tokens and cost plus
  a total, instead of just the combined headline number.

The window itself is a rounded, dark-themed, borderless panel with a draggable title bar and small
icon buttons for Refresh, Settings, and Close. It closes when it loses focus (clicking elsewhere) or
via the Close button. A footer shows "Refresh Now" and a relative "Updated Xm ago" status.

Right-click the tray icon for a context menu: **Open Dashboard**, **Refresh Now**, **Settings**, and
**Quit AIUsage**.

## Settings

The Settings window (opened from the gear icon or the tray context menu) has two sections:

- **Providers** — turn a whole provider on or off. Each row shows the provider's icon, name, and a
  toggle switch wired directly to `ProviderEnablementStore.SetEnabled`/`IsEnabled`.
- **Customize Metrics** — turn individual metrics on or off within an enabled provider (e.g. hide
  just the "Spark Weekly" row for Codex while keeping the rest). Each metric's toggle is wired to
  `LayoutStore.SetMetricEnabled`, grouped under a small header per provider.

## What is not implemented yet

None of the following from the original exist in this port:

- Menu-bar pinning/starring, or any "Bars" icon style — the underlying `LayoutStore.IsPinned`/
  `SetPinned`/`CanPin` model exists, but no UI surface exposes it yet
- Drag-reorder of providers or metrics
- Right-click row menus (hide, star, refresh one provider, rename)
- Share-screenshot, Total Spend card, keyboard shortcuts, global hotkey
- "Outdated" staleness tag (the underlying `WidgetDataStore.StalenessHint` API exists and is
  computed, but nothing in the tray UI displays it yet)
- Window-opening/closing animation, optional vibrancy/transparency style
- A rich, styled model-breakdown popover (icons, per-model sort) — the current hover tooltip is
  plain text with the same underlying data

## First launch

On a fresh install (no `%LOCALAPPDATA%\AIUsage\settings.json` yet), `AppContainer` runs the same
two-phase provider detection as the original: it enables Claude, Codex, and Cursor immediately, then
asynchronously probes every provider's local-only credential check (`HasLocalCredentialsAsync`) and
switches to exactly the detected set if anything was found. There is no onboarding card in the UI to
explain this — see [Which Providers Are On](provider-enablement.md) for the full behavior.
