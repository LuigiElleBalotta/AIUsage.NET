# AGENTS.md

AIUsage.NET is a .NET 8 / C# Windows port of [OpenUsage](https://github.com/robinebers/openusage): a
tray app that shows AI provider usage widgets (Claude, Codex, Cursor, Grok, Devin, and more). It
follows OpenUsage's architecture and behavior conventions, adapted for Windows (WPF tray app instead
of AppKit/SwiftUI, Windows Credential Manager instead of Keychain, `Microsoft.Data.Sqlite` instead of
the `sqlite3` CLI, and so on). See `PORTING_NOTES.md` for the full account of what changed and why.

This file documents the engineering conventions for the project. Read it before contributing.

## Agent Instructions

AGENTS.md is the source of truth for agent instructions in this repository. CLAUDE.md only points to
this file; do not add guidance, duplicate instructions, or project rules to CLAUDE.md.

## Architecture

- Solution of 3 projects under `src/`: `AIUsage.Core` (class library — models, providers, pricing,
  stores, services), `AIUsage.Tray` (WPF tray app), `AIUsage.Cli` (console app, `aiusage`).
- Providers implement the small `IProviderRuntime` interface: an auth store reads credentials already
  on the user's machine, a usage client calls the provider's API, and a mapper normalizes the response
  into `MetricLine` values. The tray UI and the CLI both render/read those same normalized values.
- `AppContainer` (`AIUsage.Core/App/AppContainer.cs`) is the composition root: it builds the provider
  catalog, the `WidgetRegistry`, the stores (`ProviderEnablementStore`, `LayoutStore`,
  `WidgetDataStore`), runs first-run credential detection, and owns the periodic refresh loop.
- See `PORTING_NOTES.md` for the current state of every piece (ported faithfully, simplified, or not
  yet started) and `docs/` for behavior docs.

## Providers

Conventions for the per-provider modules under `src/AIUsage.Core/Providers/<Name>/`.

- **Structure:** one folder per provider with an auth store (reads credentials already on the user's
  machine), a usage client (calls the provider API), and a mapper (normalizes to `MetricLine`),
  implementing `IProviderRuntime` — `RefreshAsync()` plus `HasLocalCredentialsAsync()`, the
  local-only credential probe used by first-run detection (`FirstRunSeeder`). Mirror the same local
  credential sources and usability filters that `RefreshAsync()` starts with; reuse the auth-store
  loaders instead of adding a second credential-reading path.
- **Model pricing:** all spend imputation (Claude, Codex, Cursor, Grok) prices through the shared
  engine in `src/AIUsage.Core/Pricing/`. Cursor-native model rates and alias rules live in
  `pricing_supplement.json`, fetched live from the upstream OpenUsage GitHub Pages URL (see
  `PORTING_NOTES.md` — this is a deliberate, approved exception to not re-branding pricing data,
  since it's public technical data maintained upstream). The bundled LiteLLM/models.dev snapshots
  regenerate with `script/update_pricing_snapshots.ps1`.
- **Default order:** Claude, Codex, Cursor first (the established providers, in that order), then
  every other provider alphabetically. See `ProviderCatalog.Make()`.
- **Metric placement defaults:** when adding or changing a metric, confirm its defaults with the
  owner before choosing — never pick silently: enabled on/off (`DefaultLayout.MetricIds`), Always
  Visible vs. On Demand (`DefaultLayout.ExpandedMetricIds`), pinned to the menu bar
  (`DefaultLayout.PinnedMetricIds`), and order (declaration order in `WidgetDescriptors`).

## Running / Testing Changes

- There is no hot reload. The tray app is a long-lived process, so every code change requires a full
  rebuild and restart to take effect. Use `script/build_and_run.ps1` (or `-Mode cli` for the CLI).
- Run `dotnet build AIUsage.sln` and fix all warnings/errors before considering a change done.

## Pull Requests

Every PR description should follow the structure in `.github/PULL_REQUEST_TEMPLATE.md`: TL;DR, what
was happening, what this changes, heads-up (optional), tests (optional), screenshots (required for
any visual/Tray UI change).

## Documentation

- Logic changes must update any docs in `docs/` that describe the affected behavior.
- Keep docs simple, less-technical, and easy to skim; exclude visual design details.
- Update `PORTING_NOTES.md` whenever a change diverges from, simplifies, or completes something
  relative to the original Swift edition — tag it `[FEDELE]`/`[SEMPLIFICATO]`/`[DIVERGENTE]`/`[OMESSO]`
  per the convention already established there.

## Code Conventions

- Add a regression test when fixing a bug, where it fits.
- Keep files under ~500 LOC; split or refactor as needed.
- No new dependencies without justification; pin exact versions.
- When adding a provider, follow the conventions in "## Providers".

## Error Handling

Always fail loudly into error logging (`AppLog`, the log file) and show friendly errors to the user
(a `MetricLine.Badge` line, not a crash). Do not add silent fallbacks that hide real problems. Only
validate at system boundaries (user input, external APIs); trust internal code and framework
guarantees.

## UI

- Use title case for any hardcoded copy used as a title.
- Match the existing design language where one exists; the WPF tray UI is currently minimal (see
  `PORTING_NOTES.md`) and open to refinement.
- Only add tooltips when explicitly asked to. Don't add them proactively to new controls.
