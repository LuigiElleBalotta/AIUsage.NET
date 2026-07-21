# Privacy & Usage Data

AIUsage.NET does not collect, transmit, or share any usage analytics or telemetry. This is a
difference from the original OpenUsage, not a configurable setting: the anonymous PostHog telemetry,
crash reporting, and the opt-out toggle described in the original's privacy docs are simply not
ported (see [PORTING_NOTES.md](../PORTING_NOTES.md)). There is nothing to turn off because nothing is
sent in the first place.

## What is stored on this PC

- **Credentials**: AIUsage.NET reads credentials that provider CLIs/apps already keep on your PC
  (config files, SQLite state databases). When it writes a user-supplied API key (OpenRouter, Z.ai)
  or a refreshed OAuth token, the file is written to your user profile
  (`~/.aiusage/<provider>.json`, or the provider's own config location) — no elevated permissions or
  system-wide storage are used. See each provider's doc under [docs/providers/](providers/) for exact
  paths.
- **Windows Credential Manager**: entries used as a fallback/alternate credential source are stored
  under target names prefixed `AIUsage:` so they don't collide with credentials from other apps.
- **Settings and cache**: `%LOCALAPPDATA%\AIUsage\settings.json` (provider enablement, layout,
  the provider snapshot cache) and `%LOCALAPPDATA%\AIUsage\pricing\` (cached pricing data). Neither
  contains credentials.
- **Logs**: `%LOCALAPPDATA%\AIUsage\Logs\AIUsage.log`. Secrets are redacted before any line is
  written — see [Logging](logging.md#what-is-never-logged).

## Network requests

- **Provider API calls** — the same calls the provider's own CLI/app/website would make, using your
  existing local credentials. See each provider's "Under the hood" section for the exact endpoints.
- **Model pricing refresh** — public price lists from `raw.githubusercontent.com` (LiteLLM),
  `models.dev`, and the upstream OpenUsage project's GitHub Pages (the pricing supplement — see
  [Model pricing](pricing.md) for why this URL is used). These carry no usage or log data.

Nothing else. There is no analytics SDK, no crash reporter, and no other background network activity.

## What never leaves your PC

- Local usage logs (Claude Code sessions, Codex rollouts, Grok's unified log, OpenCode's SQLite
  logs, Cursor's usage export) are read and priced entirely on your machine to produce the spend
  tiles. No log content is ever sent anywhere.
- No account details, credentials, or raw provider API responses are logged or transmitted beyond
  the provider call that produced them.
