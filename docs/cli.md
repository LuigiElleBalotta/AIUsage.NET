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

Output is the `openusage.limits.v1` envelope — the exact same shape served by the
[local HTTP API](local-http-api.md)'s `/v1/limits` and `/v1/limits/:id` routes (`aiusage` calls the
same routing/encoding logic in-process, so both surfaces always agree). Requesting no provider
returns every enabled provider under `providers`; requesting one provider (`aiusage codex`) returns
an envelope containing just that provider.

```jsonc
{
  "schema": "openusage.limits.v1",
  "generatedAt": "2026-07-21T13:04:22.971Z",
  "providers": {
    "codex": {
      "displayName": "Codex",
      "plan": "Free",
      "fetchedAt": "2026-07-21T13:04:22.927Z",
      "expiresAt": "2026-07-21T13:09:22.927Z",
      "stale": false,
      "resources": {
        "session": {
          "kind": "consumption", "unit": "percent",
          "used": 0, "limit": 100, "remaining": 100, "utilization": 0,
          "resetsAt": "2026-08-20T13:04:24.000Z", "windowSeconds": 2592000
        },
        "credits": { "kind": "balance", "unit": "credits", "available": 972 },
        "creditValue": { "kind": "balance", "unit": "usd", "available": 38.88 }
      }
    }
  },
  "errors": []
}
```

See [Local HTTP API](local-http-api.md) for the full field reference (`kind`, `unit`, `stale`,
per-provider resource keys) — it applies identically here.

## Install on `PATH`

Not automated yet. Either add the `aiusage.exe` build output directory to your `PATH` manually, or
build with `script/release.ps1` and place the resulting `aiusage.exe` somewhere already on `PATH`.
