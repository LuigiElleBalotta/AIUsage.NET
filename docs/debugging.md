# Debugging and Capturing Logs

How to run a local build and see what the app is doing — useful when a provider misbehaves or you're
chasing a startup or refresh problem.

## Run a local build

```powershell
script/build_and_run.ps1                        # build (Debug) and launch the tray app
script/build_and_run.ps1 -Mode build             # build only, don't launch
script/build_and_run.ps1 -Mode cli claude --force # build and run the CLI with args
script/build_and_run.ps1 -Configuration Release  # same, but a Release build
```

There is no app-bundle staging, code signing, or separate dev/release bundle identity to worry about
— this is a plain `dotnet build` followed by launching the built `.exe` directly from
`src/AIUsage.Tray/bin/<Configuration>/net8.0-windows/AIUsage.exe`.

## Watch logs live

There is no `log stream` equivalent (that's a macOS unified-logging feature). Instead, tail the file
log while reproducing an issue:

```powershell
Get-Content "$env:LOCALAPPDATA\AIUsage\Logs\AIUsage.log" -Wait -Tail 20
```

## Log file

`%LOCALAPPDATA%\AIUsage\Logs\AIUsage.log` — this is what to include in a bug report. There is no
in-app "Copy Log Path" or "Reveal in Explorer" button yet (see [dashboard.md](dashboard.md)); open
the path above directly. See [Logging](logging.md) for levels, subsystem tags, and the
never-log-secrets guarantee. Note that there is currently no size cap or rotation on this file.

## Tips

- **A provider shows an error.** Check that provider's page in `docs/providers/` for what its error
  states mean and where it reads credentials from, then check the log for its `[plugin:<name>]` /
  `[auth:<name>]` lines.
- **Nothing updates.** Refresh runs on a 5-minute timer and respects the cache; see
  [Refreshing & caching](refreshing.md). Use **Refresh Now** in the tray icon's right-click menu to
  force one.
- **Windows Credential Manager entries.** AIUsage.NET's credential entries are stored under target
  names prefixed `AIUsage:`. You can inspect them with `cmdkey /list` from a terminal, or via
  Control Panel → Credential Manager → Windows Credentials.
- **Inspect the cache directly.** `%LOCALAPPDATA%\AIUsage\settings.json` holds the persisted
  provider snapshots, layout, and enablement state as plain JSON — useful to confirm whether a
  problem is in the fetch/mapping layer or somewhere else. There is no local HTTP API to curl (not
  ported — see [architecture.md](architecture.md#local-http-api)).
