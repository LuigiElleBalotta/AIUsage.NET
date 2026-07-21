# Model Pricing

How AIUsage.NET turns token counts into the estimated dollars on the spend tiles (Claude, Codex,
Cursor, Grok). OpenRouter and OpenCode are the exceptions: OpenRouter's API reports billed dollars
directly, and OpenCode records its own per-message cost in its local logs, so nothing here applies to
them.

This is a faithful, 1:1 port of the original pricing engine (`AIUsage.Core/Pricing/`) — the layering,
resolution rules, and refresh cadence below are unchanged from the Swift edition.

## Where prices come from

Prices are layered from three sources; when the same model appears in more than one, the higher layer
wins:

1. **Pricing supplement** — a small JSON file covering models no public catalog carries (Cursor-native
   models like `auto` and `composer-*`), fast-variant multipliers, and alias rules that map provider
   log/CSV slugs to catalog keys. AIUsage.NET fetches this live from
   `https://robinebers.github.io/openusage/pricing_supplement.json` — the original project's own
   published feed. This is a deliberate choice, not an oversight: the data is public, technical, and
   maintained upstream, so continuing to consume it doesn't raise the same branding concerns as
   reusing the OpenUsage name or logo. See [PORTING_NOTES.md](../PORTING_NOTES.md).
2. **LiteLLM** — the community-maintained `model_prices_and_context_window.json`, covering the vast
   majority of API-priced models.
3. **models.dev** — a gap-filler for models LiteLLM misses.

The app ships with bundled snapshots of all three (`AIUsage.Core/Resources/pricing_*.json`), so
pricing works offline and on first launch. At runtime each source is refetched about once an hour
(with ETag revalidation) and cached in `%LOCALAPPDATA%\AIUsage\pricing\`. A refresh never blocks a
usage scan — scans always price against the freshest data already on hand.

## How a model name resolves

Log and CSV model names rarely match a catalog key exactly, so resolution tries, in order:
supplement alias rules, exact key match, fast-variant handling (a `-fast` suffix resolves the base
model and applies its fast multiplier), then fuzzy matching — provider prefixes (`anthropic/`,
`xai/`, …), dated suffixes (`claude-sonnet-4` ↔ `claude-sonnet-4-20250514`), and separator
differences (`grok-4-3` ↔ `grok-4.3`). Fast variants without an explicit price or model-specific
multiplier stay unpriced instead of silently using the standard-speed rate.

A model no source can price is left out of the spend figures entirely — its tokens don't count
toward the day's tile, the Usage Trend, or the model breakdown, because a token count next to a
dollar figure that ignores part of it would be misleading. A day where *nothing* could be priced
reads "No data".

## What the estimate includes

Costs are computed per usage event from four token buckets — plain input, cache writes, cache reads,
and output — at the model's per-million-token rates, including 1-hour cache-write pricing,
long-context tiers, and fast-variant multipliers. When a Claude log line carries an explicit
`costUSD`, that value is used as-is. The result is an estimate of API-rate value, not a bill:
subscription plans don't charge per token.

## Privacy

The pricing refresh fetches three public price lists (from `raw.githubusercontent.com`,
`models.dev`, and the upstream OpenUsage GitHub Pages URL). These requests carry no usage or log
data — nothing about your usage leaves your PC.

## Maintainer notes

- `script/update_pricing_snapshots.ps1` regenerates the bundled LiteLLM/models.dev snapshots from
  the live feeds. Run it occasionally (e.g. before a release); staleness is otherwise harmless since
  runtime fetches override the bundled copies.
- Supplement changes are not made in this repository — the supplement is fetched live from the
  upstream project. If a model needs a correction that only affects this port (e.g. a Cursor-native
  model AIUsage.NET needs but the upstream supplement doesn't carry), that would require either
  bundling a local override or forking the supplement fetch — not yet designed, see
  PORTING_NOTES.md.
