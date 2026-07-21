# Command-Line Interface

AIUsage.NET ships a one-shot `aiusage` command for agents and scripts. It reads through the shared
provider cache and exits; it never launches or leaves the tray app running.

```powershell
aiusage                 # every provider, refreshing stale cache entries
aiusage codex           # one provider, refreshing when its cache is stale
aiusage codex --force   # refresh through the shared provider engine, cache, print, exit
```

The command and the tray app import the same providers, authentication stores, pricing, and snapshot
cache. A normal read reuses snapshots less than five minutes old and refreshes missing or stale ones.
`--force` bypasses that freshness gate and writes successful results to the same cache file the tray
app reads (`%LOCALAPPDATA%\AIUsage\settings.json`), so both surfaces always agree on what's fresh.
Credentials are used locally and never appear in the output.

Options:

```
Usage: aiusage [provider] [--force]

  --force      Refresh even when the shared cache is still fresh
  -v, --version
  -h, --help
```

Exit codes are `0` for success, `2` for invalid arguments or an unknown provider, and `4` when a
refresh produced a warning or a provider-level error.

## Output shape

Output is JSON. Requesting no provider returns an object keyed by provider ID, each value the
provider's normalized snapshot (`displayName`, `plan`, `lines`, `refreshedAt`, `warning`, `error`).
Requesting one provider (`aiusage codex`) returns just that provider's snapshot object directly, not
wrapped in a keyed envelope.

```jsonc
{
  "displayName": "Codex",
  "plan": "Free",
  "lines": [
    { "label": "Session", "type": "progress", "used": 0, "limit": 100,
      "format": { "kind": "percent" }, "resetsAt": "2026-08-20T09:37:38+00:00",
      "periodDurationMs": 2592000000 },
    { "label": "Credits", "type": "values",
      "values": [ { "Number": 38.88, "Kind": "Dollars" }, { "Number": 972, "Kind": "Count", "Label": "credits" } ] }
  ],
  "refreshedAt": "2026-07-21T09:37:37.0441133+00:00",
  "warning": null,
  "error": null
}
```

Line types are `text`, `values`, `progress`, `badge`, and `chart` — the same shapes described in
[the `MetricLine` model](../src/AIUsage.Core/Models/MetricLine.cs). This is a direct JSON dump of
`ProviderSnapshot` rather than the original's dedicated `/v1/limits` stable-resource-ID envelope — the
local HTTP API that format was designed to share with is not ported (see
[architecture.md](architecture.md#local-http-api)), so `aiusage`'s output shape may still change if
that's added later.

## Install on `PATH`

Not automated yet. Either add the `aiusage.exe` build output directory to your `PATH` manually, or
build with `script/release.ps1` and place the resulting `aiusage.exe` somewhere already on `PATH`.
