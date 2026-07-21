# Which Providers Are On

How AIUsage.NET decides which providers start on, and what happens when an update adds a new
provider. This logic (`FirstRunSeeder`) is a faithful, 1:1 port of the original's first-run/new-provider
detection — the difference from the original is that there is currently **no Customize UI** to turn
providers on or off afterward (see [dashboard.md](dashboard.md)); the enabled set can only be changed
by editing `%LOCALAPPDATA%\AIUsage\settings.json` directly or by calling
`ProviderEnablementStore.SetEnabled` from code.

## First install

A fresh install (no `settings.json` yet) doesn't turn on every provider AIUsage.NET knows about. It
starts with Claude, Codex, and Cursor, then quickly checks which providers have credentials available
on your PC — an existing local login, saved API key, or supported environment variable; nothing is
sent anywhere — and switches to exactly that set. All providers are checked concurrently, so
detection takes as long as the slowest single check, not the sum of them. If nothing is found, the
Claude/Codex/Cursor starter set stays.

## When an update adds a new provider

The same detection (`FirstRunSeeder.Reseed`) can run for providers that arrive later, but nothing in
the tray app currently calls it automatically on every launch the way the original's
`NewProviderSeeder` does — this piece (comparing "providers I now ship" against "providers this
install has ever seen") is not yet wired into `AppContainer`'s startup path. See
[PORTING_NOTES.md](../PORTING_NOTES.md).

## Your choices always stick

Once a provider's enabled state has been set (by first-run detection, or manually), nothing in the
app currently overrides it automatically. There is no "Reset All Customization" action wired up to a
UI button yet, though the underlying `FirstRunSeeder.Reseed` method that would back such a button
already exists and works the same way as the original: it snaps the enabled set to the
Claude/Codex/Cursor fallback, then re-probes local credentials and switches to exactly the detected
set.

## How it works (for the curious)

The app persists two small lists in `ProviderEnablementStore` (backed by
`%LOCALAPPDATA%\AIUsage\settings.json`):

- **Enabled providers** (`aiusage.enabledProviders.v1`) — the providers currently on. `null` means
  "never customized" (nothing seeded yet); a fresh install starts in this state.
- **Known providers** (`aiusage.knownProviders.v1`) — every provider this install has ever seen. This
  is what would make "new in this update" distinguishable from "you turned it off," once new-provider
  detection is wired into the startup path (see above).

Each provider implements a cheap, local-only credential probe (`HasLocalCredentialsAsync()`) — the
same files, Windows Credential Manager entries, saved keys, and environment variables its normal
refresh reads, never the network.
