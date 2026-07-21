# Claude

Tracks your Claude subscription limits using the login you already have from Claude Code.

## What it tracks

| Metric | Meaning |
|---|---|
| Session | 5-hour rolling window usage |
| Weekly | 7-day window usage |
| Sonnet | Separate weekly Sonnet limit (plan-dependent) |
| Fable | Separate weekly Fable limit (model-scoped window from the `limits` array) |
| Extra Usage | Extra-usage credits spent against your monthly cap |
| Today / Yesterday / Last 30 Days | Local spend, as cost, tokens, or both (see below) |

When Claude reports your plan name, AIUsage.NET shows it beside the provider name.

## Where credentials come from

Sign in with Claude Code; AIUsage.NET reads the existing login. It checks these sources, preferring one that can read your subscription usage:

1. `~/.claude/.credentials.json` (or `$CLAUDE_CONFIG_DIR/.credentials.json`) — Claude Code's own credential file, and the primary source on Windows
2. The Windows Credential Manager entry Claude Code may maintain (checked as a secondary source, in case a future Claude Code version stores it there)
3. `CLAUDE_CODE_OAUTH_TOKEN` environment variable

A `CLAUDE_CODE_OAUTH_TOKEN` — usually a long-lived `claude setup-token` — can run the model but can't read your Session and Weekly limits, and it often lingers in your shell environment. So when a real file or Credential Manager login is present, AIUsage.NET uses that login for the live meters and keeps the environment token only as a fallback; the Session/Weekly meters no longer go blank just because that token is set. If the environment token is your *only* credential (a headless setup), it's used on its own and the spend tiles still load from local logs.

If one source holds an expired or "locked out" token, AIUsage.NET falls back to the others — so signing in again with `claude` outside the app is picked up on the next refresh, without restarting AIUsage.NET. Claude Code tokens are refreshed automatically; rotated tokens are written back only while the ordered login candidates still match the start of the refresh, so a newly added higher-priority login wins.

## The spend tiles

Today / Yesterday / Last 30 Days are computed **locally**: AIUsage.NET reads the Claude Code session logs under `~/.claude/projects/` (or `$CLAUDE_CONFIG_DIR`) itself — no external tools needed. Symlinks are followed, so a projects folder linked into a synced location (say, a OneDrive folder) is read all the same. Persisted `claude -p` runs count as well. Runs made with `--no-session-persistence` cannot appear because Claude deliberately writes no session log for AIUsage.NET to read. Advisor work recorded inside a message is counted once under the advisor's own model; the parent's main-model totals are kept separate, and ordinary iteration details are not counted again. A log's recorded fast or standard speed controls its price; AIUsage.NET does not infer speed from the event date. Days are grouped in your PC's local time zone, so they line up with your own calendar. Each period is one tile showing cost and tokens together (`$4.08 · 1.2M tokens`); a day with no usage reads **No data** rather than a misleading `$0.00 · 0 tokens` — the same as every other spend-tracking provider. The live Session and Weekly meters are unaffected. The dollars are estimated from token counts at API rates (that's the ⓘ) using the shared [model pricing](../pricing.md); the token counts themselves are measured. No log data leaves your PC.

## Troubleshooting

- **"Not logged in"** — run `claude` and sign in, then refresh.
- **"Re-login for live usage"** (an amber warning on the Claude header) — your saved login can authenticate for inference but can't read your subscription limits, because it lacks the `user:profile` access (this is what an inference-only token from `claude setup-token` carries). Run `claude` and sign in again with your Claude account, then refresh; the spend tiles keep working in the meantime.
- **"Updates blocked by Anthropic"** (an amber warning on the Claude header) — the usage API is throttling AIUsage.NET. It keeps the last values from the same login, shows when it will retry, and backs off in the meantime. A different login starts with a fresh cache and cooldown.
- **Spend tiles show "No data"** — AIUsage.NET found no Claude Code logs in the last 30 days. If your logs live somewhere custom, set `CLAUDE_CONFIG_DIR` so both Claude Code and AIUsage.NET look in the same place.

## Under the hood

`GET https://api.anthropic.com/api/oauth/usage` with the selected OAuth token. Claude Code tokens refresh via `platform.claude.com/v1/oauth/token`. If a token is expired or revoked, AIUsage.NET retries with the next credential source before reporting an error.

When the five-hour session window has no usage yet, the Session row shows **Not started** on the trailing label; hover explains that the session begins after your first message.
