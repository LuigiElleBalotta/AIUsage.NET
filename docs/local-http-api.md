# Local HTTP API

AIUsage.NET exposes a read-only HTTP API on the loopback interface so other local apps can consume
the same usage data shown in the tray dashboard.

**Base URL:** `http://127.0.0.1:6736`

The server starts automatically with the tray app (`AIUsage.exe`). If the port is already in use,
the feature is silently disabled for that session — check the log (see [Logging](logging.md)) for
a `[localapi] disabled: ...` line if requests fail to connect.

The CLI (`aiusage`) does not run this server — it calls the exact same routing/encoding logic
in-process instead, so `aiusage <provider>` and a request to `/v1/limits/<provider>` always produce
identical JSON.

## Routes

### `GET /v1/limits`

Returns a machine-facing envelope for all **enabled** providers. Providers and resources are keyed
by stable IDs; values are raw scalars with explicit units. This is the preferred route for new
integrations and the exact format printed by the `aiusage` CLI.

### `GET /v1/limits/:id`

Returns the same envelope containing the provider the ID names.

- **200 OK** — limits envelope with the matched provider if it has data (an `errors` entry appears
  when a refresh failed; a matched provider with no data yet simply has no entry).
- **404 Not Found** — the ID names no known provider.

> This port currently matches by exact provider ID only (`claude`, `codex`, `cursor`, etc.) — there
> is no multi-account support yet, so there is no "family ID naming several account cards" case to
> handle (see [PORTING_NOTES.md](../PORTING_NOTES.md)).

### `GET /v1/usage`

Returns the legacy UI-oriented snapshots for all **enabled** providers, in dashboard order. Existing
consumers remain supported while this route is deprecated; new consumers should use `/v1/limits`.

- **200 OK** — JSON array (may be empty `[]` if nothing has been fetched yet).

### `GET /v1/usage/:id`

Returns the latest snapshot for the provider the ID names, as a one-element array (or `[]` if the
provider has no snapshot yet).

- **200 OK** — JSON array, `[]` when the matched provider has no snapshot yet.
- **404 Not Found** — the ID names no known provider.

### Everything else

Methods other than `GET`/`OPTIONS` return **405**; unknown routes return **404**. When the server
is already handling its maximum of 16 concurrent connections, requests get **503** — back off and
retry.

## Limits response shape

```jsonc
{
  "schema": "openusage.limits.v1",
  "generatedAt": "2026-07-21T13:11:52.418Z",
  "providers": {
    "claude": {
      "displayName": "Claude",
      "plan": "Pro",
      "fetchedAt": "2026-07-21T13:11:46.940Z",
      "expiresAt": "2026-07-21T13:16:46.940Z",
      "stale": false,
      "resources": {
        "session": {
          "kind": "consumption",
          "unit": "percent",
          "used": 27,
          "limit": 100,
          "remaining": 73,
          "utilization": 0.27,
          "resetsAt": "2026-07-21T17:39:59.596Z",
          "windowSeconds": 18000
        }
      }
    }
  },
  "errors": []
}
```

`kind` is `consumption` (`used`) or `balance` (`available`). Bounded consumption also carries
`limit`, `remaining`, and a 0–1 `utilization`. Reset, window, and `estimated` fields appear only
when the provider supplies that meaning. A provider or resource with no current value is omitted
rather than invented as zero. `expiresAt` is always `fetchedAt` plus the same five-minute freshness
interval used by the tray app and CLI; `stale` says whether that instant has passed. Refresh
failures appear in `errors` as `{"providerId":"…","message":"…"}` while a last-good provider
snapshot remains available.

For bounded progress resources, `unit` follows the provider's live metric format. For example,
Cursor `totalUsage` is `percent` on percentage-based plans, `requests` on request-based Enterprise
plans, and `usd` when Cursor reports a dollar pool.

### Public resources

| Provider | Resource keys |
| --- | --- |
| Claude | `session`, `weekly`, `sonnet`, `fable`, `extraUsage` |
| Codex | `session`, `weekly`, `spark`, `sparkWeekly`, `credits`, `creditValue` |
| Cursor | `totalUsage`, `autoUsage`, `apiUsage`, `onDemand`, `requests`, `credits` |
| Antigravity | `geminiSession`, `geminiWeekly`, `nonGeminiSession`, `nonGeminiWeekly` |
| Copilot | `premiumCredits`, `extraUsage`, `orgCredits`, `orgSpend`, `chat`, `completions` |
| Devin | `daily`, `weekly`, `extraUsageBalance` |
| Grok | `weekly` |
| OpenCode | `session`, `weekly`, `monthly` |
| OpenRouter | `credits`, `balance`, `keyLimit` |
| Z.ai | `session`, `weekly`, `webSearches` |

Charts, colors, subtitles, formatted badges, layout state, and historical spend periods stay out of
this contract. Codex's combined Credits UI row becomes two scalar resources: `credits` and
`creditValue`.

## Legacy usage response shape

```jsonc
{
  "providerId": "claude",
  "displayName": "Claude",
  "plan": "Pro",
  "lines": [
    {
      "type": "progress",
      "label": "Session",
      "used": 27.0,
      "limit": 100.0,
      "format": { "kind": "percent" },          // or "dollars", or "count" (+ "suffix")
      "resetsAt": "2026-07-21T17:39:59.596Z",   // optional
      "periodDurationMs": 18000000,             // optional
      "color": null
    },
    {
      "type": "text",
      "label": "Today",
      "value": "$23.15 · 38.1M tokens",
      "color": null,
      "subtitle": null
    },
    {
      "type": "badge",
      "label": "Pay as you go",
      "text": "20 cap",
      "color": "#22c55e",
      "subtitle": null
    },
    {
      "type": "barChart",
      "label": "Usage Trend",
      "points": [
        { "label": "Jul 20", "value": 3858734.0, "valueLabel": "3.9M tokens" },
        { "label": "Jul 21", "value": 38147756.0, "valueLabel": "38.1M tokens" }
      ],
      "note": "From your Claude usage history (estimated)",
      "color": null
    }
  ],
  "fetchedAt": "2026-07-21T13:11:46.940Z"
}
```

Line types are `progress`, `text`, `badge`, and `barChart`. A `barChart` line carries a `points`
array — one `{ label, value, valueLabel? }` per day, oldest first — plus an optional `note`;
`value` is the day's token count, `valueLabel` its pre-formatted readout, and `label` a localized
month/day (e.g. "Jul 21"). `fetchedAt` is when the snapshot was last fetched successfully
(ISO 8601).

The in-app model breakdown shown when hovering spend rows is not included in this API. Spend rows
continue to serialize as the same `text` lines so existing local integrations keep their current
shape.

`displayName` is the card's current name. Match on `providerId`, never on the name.

## Errors

```json
{ "error": "provider_not_found" }
```

Codes: `provider_not_found`, `not_found`, `method_not_allowed`, `server_busy`.

## CORS and privacy

All responses include permissive CORS headers (`Access-Control-Allow-Origin: *`, methods
`GET, OPTIONS`). `OPTIONS` requests return **204** for preflight.

The server only listens on the loopback interface (`127.0.0.1`), so it is not reachable from other
machines on your network. Because the CORS header is permissive, though, a web page open in your
browser can read your usage snapshots from this API while the app is running. The data exposed is
the same usage numbers shown in the tray dashboard — no credentials or tokens are ever served.

## Caching behavior

The API serves whatever the app is showing: only successful fetches replace data, so a failed
refresh never blanks the API — you keep getting the last good snapshot. See
[Refreshing & caching](refreshing.md).
