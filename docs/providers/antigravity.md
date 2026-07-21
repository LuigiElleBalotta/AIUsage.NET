# Antigravity

Tracks pool quotas for Antigravity (Google's AI IDE) using credentials the app or the `agy` CLI already stored on your PC.

## What it tracks

Antigravity has two shared quota pools, and each pool has two windows — a rolling 5-hour window and a weekly window:

| Metric | Meaning |
|---|---|
| Session | The shared Gemini pool (Pro and Flash draw from the same quota), rolling 5-hour window |
| Weekly | The same Gemini pool's weekly window |
| Claude | The shared non-Gemini pool (Claude, GPT-OSS, …), rolling 5-hour window |
| Claude Weekly | The same non-Gemini pool's weekly window |

When Antigravity reports your subscription tier (such as `Pro` or `Ultra`), AIUsage.NET shows it beside the provider name.

Gemini Pro and Gemini Flash are one pool: using either model drains the same quota, so AIUsage.NET shows one meter per window instead of separate Pro and Flash meters. That pair is named Session and Weekly to match the other providers' rows. Every non-Gemini model shares the second pool, shown under the Claude name (like Codex's Spark pair). Quotas are reported as a fraction (full = 0% used), so there are no token or dollar spend tiles.

While a pool's rolling 5-hour window has no usage yet, that meter reads **Not started** on the trailing label instead of a reset countdown; hover explains that the session begins after your first message. The weekly meters always show a normal reset countdown.

## Where credentials come from

AIUsage.NET never asks for a token — it reads what Antigravity already has:

- **Antigravity running** — AIUsage.NET talks to the app's local language server (the richest source, and where the plan name comes from). It's discovered by scanning running processes for `language_server` / `agy` (via WMI's `Win32_Process.CommandLine`, since Windows has no single `ps`-equivalent command) and mapping the matching process's PID to its listening TCP ports with `netstat -ano`.
- **App closed** — it falls back to the OAuth token Antigravity / `agy` store in Windows Credential Manager and queries Google's Cloud Code API. An expired token is refreshed automatically (AIUsage.NET never writes back to Antigravity's own credential entry). Its short-lived cache is reused only while the same Credential Manager login is present and readable.

If neither is available you'll see *Start Antigravity or run `agy` and try again.*

## Troubleshooting

- **"Start Antigravity or run `agy`…"** — sign in to the Antigravity app (or run `agy`) so a usable token exists, then refresh.
- **"Couldn't read Antigravity credentials…"** — sign in to Antigravity again so Windows Credential Manager holds a readable entry. AIUsage.NET will not use its cached access token until the current login can be verified.
- **The weekly meters show "No data"** — your Antigravity build doesn't expose the quota-summary endpoint yet (only newer builds do). The 5-hour meters still work from the older endpoints; updating Antigravity brings the weekly meters back.
- **A meter shows "No data"** — that pool/window wasn't in the latest response (some tiers only report certain windows). The other meters still update.
- **Where did the Gemini Pro and Flash meters go?** — merged: both models draw from the one shared Gemini pool, which is now the single Session meter.
- **Quotas look full after heavy use** — the 5-hour windows reset on a rolling basis and the weekly windows once a week; the reset time is shown on each meter.

## Under the hood

Best source first: the local language server discovered via WMI process enumeration and `netstat -ano` port mapping for the `language_server` / `agy` process, reading its CSRF token and listening ports; then Google Cloud Code using the Windows Credential Manager token, refreshed via Google OAuth when needed. AIUsage.NET binds its short-lived refreshed-token cache to a one-way fingerprint of the current Credential Manager refresh credential. Logout, account changes, legacy caches, and expired or malformed entries cannot reuse a previous account's access token. On each source AIUsage.NET asks the quota-summary endpoint first (`RetrieveUserQuotaSummary` on the language server, `v1internal:retrieveUserQuotaSummary` on Cloud Code) — the only endpoint that reports the merged pools and the weekly windows. Builds without it fall back to the legacy per-model endpoints (`GetUserStatus` / `GetCommandModelConfigs` locally, `fetchAvailableModels` / `retrieveUserQuota` remotely), whose per-model quotas are merged into the two pools by keeping each pool's worst remaining fraction; those endpoints only know the 5-hour windows. The plan name prefers Antigravity's own `userTier` over the inherited Windsurf plan field.

> Reverse-engineered from the app and language-server binary; endpoints and storage may change without notice.
