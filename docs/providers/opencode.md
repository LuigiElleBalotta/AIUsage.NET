# OpenCode

Tracks your OpenCode-hosted usage: the **Go** subscription's server-reported quota and the **Zen**
pay-as-you-go gateway's locally recorded spend.

## What it tracks

| Metric | Meaning |
|---|---|
| Session | Go usage in the current 5-hour window, against the limit and reset OpenCode Console reports |
| Weekly | Go usage in the current week, against the server-reported limit and reset |
| Monthly | Go usage in the current billing cycle, against the server-reported limit and reset |
| Today / Yesterday / Last 30 Days | Local cost and tokens across all OpenCode-hosted usage on this PC (Go + Zen), grouped by the machine's local calendar day |
| Usage Trend | A day-by-day sparkline of local tokens over the last month, on the same local calendar days |

When your account has an active Go or Go Plus subscription, AIUsage.NET shows the plan name beside the
provider. The plan name and all three meter numbers come from the same server response; AIUsage.NET
does not assume a fixed dollar limit for any plan. Accounts can have different normalized limits, so
the server's numbers are the only ones used.

## Server quota versus local spend

The Session / Weekly / Monthly meters are **authoritative account-wide quota** from OpenCode's own
Console. The Today / Yesterday / Last 30 Days figures are **local spend from this PC's logs**. They are
deliberately different measurements:

- Local logs may miss sessions from other machines, unsynced work, or a session OpenCode has not
  finished writing.
- Local spend is bucketed by this PC's **local calendar day**, including daylight-saving changes.
  The Console meters instead use the server's absolute window/reset timestamps; those resets are
  not local-day boundaries and can look different around midnight or a DST transition.
- OpenCode's per-message local `cost` is useful for local spend, but it is not the normalized billing
  figure behind the Console quota. Do not expect the two dollar totals to match.
- If the Console quota cannot be read, AIUsage.NET shows a **Go Quota** badge on the OpenCode card
  instead of drawing a progress bar with a guessed limit or reset. Local spend tiles continue to work.
  A quota request or saved-credential failure is also reported in the local `/v1/limits` `errors`
  list and as a warning from the `aiusage` CLI; it is a partial issue, not a full provider failure, so
  local spend and the card's quota status remain available.
- If the local database cannot be read, the authoritative Console meters are still requested and
  shown, with a **Local Usage: Unavailable** badge on the card instead of inventing zero history.
- Some messages can be recorded before OpenCode writes their `cost`. When only some messages have a
  cost, the dollar figures are **known subtotals** of those messages, not a complete total. The card
  shows **Local Usage: Partial Cost** and the model breakdown note says the same. Messages without a
  cost still contribute their tokens; AIUsage.NET never invents a `$0.00` cost for them. If no
  message has a cost, the spend rows show tokens only.

When a refresh fails completely (for example OpenCode's local database or credentials are gone), the
card shows **Refresh Failed** first and keeps the last successful snapshot. Any qualifiers on that
snapshot, such as **Local Usage: Partial Cost**, stay visible after the failure badge, so the retained
numbers keep their original caveats.

The Session / Weekly / Monthly tiles show the quota as **percent left** (for example, `94% left`),
matching the percentage view on the OpenCode Go web page. The underlying server quota is still a
normalized USD amount: AIUsage.NET keeps the provider's raw dollars internally, the local
`/v1/limits` API still reports `usd` limits/used/remaining values, and the local spend tiles
(Today / Yesterday / Last 30 Days) remain dollars. Only the tile headline changes.

OpenCode Console's quota route is an internal route used by the OpenCode website, not a documented
public API. It may change without notice. AIUsage.NET treats any failure as "quota unavailable" and
never substitutes a hardcoded plan.

## After an upgrade

Older versions estimated the Session / Weekly / Monthly meters from local spend with fixed limits.
Those estimates were written into AIUsage.NET's local snapshot cache, which survives upgrades. A
cached snapshot with no current quota-version marker is treated as **incompatible quota data**, even
when its timestamp looks recent: the three estimated meters and any old plan label are discarded on
read, and the card shows **Go Quota: Refresh Required** (also reported in the local API `errors` and
CLI warnings) until a fresh refresh replaces the legacy entry.

Local spend tiles, qualifiers such as **Local Usage: Partial Cost**, usage history, and the original
timestamp are kept, so the Usage Trend and history backup stay intact. The sanitized entry is never
counted as fresh: the next refresh (including a normal `aiusage` read and the tray's refresh loop)
checks current credentials and asks OpenCode Console when they are usable, instead of returning the
old numbers. A fresh result replaces the migration badge with the server quota, **No Active Go**, or
the applicable credential/network status. It stores the current marker; no old inferred value is
re-labelled as server data.

## Where credentials come from

AIUsage.NET only reads credentials already on your machine. It never refreshes, rotates, or writes an
OpenCode token, and it never starts a login flow, so refreshing AIUsage.NET cannot sign OpenCode out.

- **OpenCode 2.x** keeps active integrations in the `credential` table of its local SQLite database
  (`integration_id = 'opencode'`, active rows). OAuth and API-key entries are supported. The
  credential's `metadata.orgID` is sent as `x-org-id`; a credential without it may be rejected by the
  Console, in which case the quota is shown as unavailable.
- **OpenCode 1.x** keeps `auth.json` in the data directory. The `opencode-go` API key is still read for
  local detection, and an `opencode` Console OAuth/API-key entry is used for the server quota when it
  has the metadata the Console needs. Old Go inference keys may not be accepted by the Console; in that
  case AIUsage.NET shows the quota as unavailable rather than inventing one.
- Credentials from other integrations or workspaces are never borrowed. AIUsage.NET picks the newest
  active V2 row first and reports that row's state. If it is expired, invalid, or points at an
  unsupported origin, it shows **Reconnect/Unavailable** rather than falling back to an older row, a
  different workspace, or V1 `auth.json`. If any V2 database or active credential row cannot be read
  reliably, the credential is reported as unreadable or invalid instead of borrowing a row from
  another database or silently using V1. An expired credential is never refreshed.

Data directory resolution: `$OPENCODE_DATA_DIR` wins, then `$XDG_DATA_HOME\opencode`, then the canonical
`%USERPROFILE%\.local\share\opencode` (the path OpenCode 2.x reports with `opencode debug paths`). The
older `%LOCALAPPDATA%\opencode` is used when the canonical directory is absent but the older one exists.
The two locations are never merged.

## Quota request

When a usable Console credential exists, AIUsage.NET sends one read-only request to:

```
GET https://opencode.ai/console/api/go/status
Authorization: Bearer <credential>
x-org-id: <workspace>
```

Only the official HTTPS Console origin is used. A credential whose metadata points anywhere else is
rejected before a request is made, `https://console.opencode.ai` is treated as the retired alias of the
canonical origin, and redirects are not followed. Requests have an explicit timeout; local logs are
never uploaded.

The Console declares its subscription response as nullable. A literal `null` (or an explicit
`access: null`) means the account has no active Go subscription, so AIUsage.NET shows **No Active Go**
and does not keep a plan label from old local Go history. A non-null object with a missing `access`
field is treated as an unexpected contract change instead. Before the first five-hour use the Session
window may have no start/reset timestamp; its real used and limit values are still shown, without a
guessed countdown. Week and month resets are required. Supplied start/reset and access bounds must be
ordered, or the response is rejected rather than partially rendered.

## Log formats and versions

AIUsage.NET detects the schema from each database instead of trusting the installed CLI version:

- **V1** `message` table: assistant rows with `$.role`, `$.providerID`, `$.modelID`, `$.cost`, and
  `$.tokens.total`.
- **V2** `session_message` table: `type = 'assistant'` rows with `$.model.providerID`, `$.model.id`,
  `$.cost`, and separate input / output / reasoning / cache-read / cache-write token counters.

During migration OpenCode can keep the same message ID in both tables, and release-channel databases
can contain overlapping copies. AIUsage.NET deduplicates message IDs with V2 winning regardless of
timestamps. Within one schema generation the newest `time_updated` wins, falling back to
`time_created` when it is missing or null; exact ties use the ordinal-greater source path so results
never depend on database enumeration order. An empty assistant row still suppresses an older copy of
the same message, but a selected row with no cost and no tokens is not counted, so it cannot create a
fake empty day. In-progress assistant rows without a cost still contribute their tokens; they do not
create a fake `$0.00` cost. Token counts are summed in 64-bit integers across messages, days, and
model breakdowns so multi-billion-token histories never wrap or overflow.

The installed CLI version is probed once per app lifetime with a bounded `opencode --version` call. It
looks on `PATH` and in known per-user install locations, including `~/.opencode/bin/opencode.exe`,
`~/.local/bin`, `~/.bun/bin`, and `%LOCALAPPDATA%\Programs\opencode`. It is only logged as a diagnostic
hint; a missing, malformed, or timed-out CLI does not hide readable logs. Unknown database schemas are
reported as **Unsupported Schema** rather than shown as zero usage. A query result with malformed JSON
or out-of-range numbers is logged without its contents and surfaced as **Partial Data** on the card
alongside any readable spend, never silently treated as an empty day.

## Troubleshooting

- **Everything shows "No data"** — AIUsage.NET needs OpenCode's local database at
  `%USERPROFILE%\.local\share\opencode\opencode*.db` (or in your `$OPENCODE_DATA_DIR` /
  `$XDG_DATA_HOME\opencode` if set). Run an OpenCode session, then refresh.
- **No Session / Weekly / Monthly meters** — those meters need an active Go/Go Plus subscription and a
  usable Console credential. Zen-only users, lapsed subscriptions, and V1-only Go keys see the spend
  tiles instead plus a **Go Quota: Unavailable/Reconnect** badge when appropriate.
- **"Go Quota: Unavailable"** — the Console could not be reached, rejected the saved credential, or the
  internal quota route changed. Local spend is still shown; run OpenCode to sign in again if the badge
  says Reconnect. The same reason appears in the local API's `errors` list and in CLI warnings.
- **"Go Quota: No Active Go"** — the Console says the account has no active Go subscription. No meters
  are fabricated, and this is not treated as an error.
- **"Go Quota: Refresh Required"** — the cached quota came from an older AIUsage.NET version that
  inferred it locally. The old meters and plan label were removed; local spend and history are still
  shown. Refresh (or wait for the next automatic refresh) to get the current Console quota.
- **"Local Usage: Partial Cost"** — some local messages have no recorded cost. Tokens are summed for
  every message, but the dollar figures only cover messages that do have a cost, so they are known
  subtotals rather than a complete total. Missing costs are not guessed.
- **"Local Usage: Unsupported Schema"** — the database has tables AIUsage.NET does not recognize.
  Update OpenCode or AIUsage.NET. **Partial Data** means at least one database or query result could
  not be read while the rest was usable. **Unavailable** means local usage failed entirely; any
  Console quota that could be fetched is still shown.
- **"Couldn't read OpenCode's local database"** — quit OpenCode and refresh; check permissions on the
  data directory.
- **Numbers look lower than your dashboard** — the spend tiles are local spend from this PC only; the
  quota meters are account-wide server numbers. See the note above.

## Under the hood

All `opencode*.db` files in the resolved data directory are read (OpenCode partitions by release
channel, e.g. `opencode.db` and `opencode-next.db`). The scanner probes `sqlite_master` per file,
chooses the V1 and/or V2 reader, and unions the rows. Both Go (`opencode-go`) and Zen (`opencode`) rows
feed the spend tiles; only Go rows are relevant to the Go quota. Local rows are converted to the
machine time zone and grouped by local date, with query cutoffs computed as exact local-midnight
instants. Spent dollars come from OpenCode's own per-message cost, not an AIUsage.NET price table.
Local cost is never used to guess a quota limit or a reset time. The scan also reports whether any
selected message inside the rendered 30-day window has no cost; suppressed duplicate rows and rows
outside the window do not qualify the subtotal as incomplete.
