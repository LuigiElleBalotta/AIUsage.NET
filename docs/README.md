# AIUsage.NET Documentation

What the app does and how it behaves. These pages describe **behavior, not visuals**, and should be
updated together with any change to that behavior. Where a feature from the original OpenUsage isn't
ported yet, the page says so explicitly rather than describing something that doesn't exist — see
[PORTING_NOTES.md](../PORTING_NOTES.md) for the authoritative, continuously updated list.

## The app

- [Dashboard](dashboard.md) — the tray popup window: what it shows today, and what a full dashboard would add
- [Refreshing & caching](refreshing.md) — when data updates and what happens when a fetch fails
- [Model pricing](pricing.md) — how spend tiles price tokens, and where the rates come from
- [Privacy & usage data](privacy.md) — what leaves your PC, and what never does

## Integrations

- [Command-line interface](cli.md) — one-shot cached and forced usage reads for agents and scripts
- [Local HTTP API](local-http-api.md) — a read-only loopback API other local apps can consume
- [Proxy](proxy.md) — route provider requests through HTTP(S) (SOCKS5 is read but degraded)

## Providers

What each provider tracks, where its credentials come from, and what to do when it shows an error.

- [Antigravity](providers/antigravity.md)
- [Claude](providers/claude.md)
- [Codex](providers/codex.md)
- [Copilot](providers/copilot.md)
- [Cursor](providers/cursor.md)
- [Devin](providers/devin.md)
- [Grok](providers/grok.md)
- [Kiro](providers/kiro.md)
- [OpenCode](providers/opencode.md)
- [OpenRouter](providers/openrouter.md)
- [Z.ai](providers/zai.md)

## For developers

How the app is built and how to extend it.

- [Architecture](architecture.md) — composition root, stores, the provider pipeline, the WPF/tray bridge
- [Adding a provider](adding-a-provider.md) — the metric contract and the register/test/document steps
- [Debugging & capturing logs](debugging.md) — running a local build and reading the log file
- [Logging](logging.md) — the file log, subsystem tags, and what is never logged
