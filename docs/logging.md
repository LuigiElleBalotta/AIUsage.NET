# Logging

AIUsage.NET keeps a file log so you can capture what the app was doing and share it when something
misbehaves. This is a faithful port of the original's `AppLog` design (levels, subsystem tags,
redaction), minus log rotation and a Settings UI to change the level — see below for what differs.

## Where the log file lives

```
%LOCALAPPDATA%\AIUsage\Logs\AIUsage.log
```

`AIUsage.Tray`'s `App.xaml.cs` calls `AppLog.Bootstrap(logPath)` at startup, which creates the
directory if needed and writes an initial `AIUsage starting (level=Info, log=...)` line. The CLI
(`aiusage`) does not currently call `Bootstrap`, so CLI runs are not logged to this file — only the
tray app writes to it.

## Changing the log level

There is no Settings UI to change this yet (unlike the original's Settings → Advanced → Log Level
picker). The level defaults to `Info` and can currently only be changed by calling
`AppLog.SetLevel(...)` from code, or by changing the default passed to `AppLog.Bootstrap` in
`App.xaml.cs`.

| Level | What it captures |
|---|---|
| Error | Only failures. |
| Warn | Failures plus things that look wrong but recovered. |
| Info | The normal story: refresh start/end, per-provider results, cache and auth milestones. |
| Debug | Everything, including per-request and per-cache-check detail. |

## Subsystem tags

Every line is prefixed with a bracketed tag so the log is easy to search (see `LogTag` in
`AIUsage.Core/Support/AppLog.cs`):

`[refresh]` `[cache]` `[http]` `[auth]` `[credentialstore]` `[trayicon]` `[updates]` `[config]`
`[statusitem]` `[localapi]` `[subprocess]` `[lifecycle]` `[notifications]` `[pricing]`, plus
per-provider tags like `[plugin:claude]` and `[auth:claude]`.

For example, to follow just the refresh cycle in PowerShell:

```powershell
Select-String -Path "$env:LOCALAPPDATA\AIUsage\Logs\AIUsage.log" -Pattern '\[refresh\]'
```

## What is never logged

Secrets never reach the log. `LogRedaction.RedactLogMessage` (called on every line `AppLog` emits)
redacts JWTs, `sk-`/`pk-`/`api_`/`key_`/`secret_`-prefixed values, Devin session tokens, and
`account=` values before the line is written — a sensitive value becomes `first4...last4`, or
`[REDACTED]` when too short to mask safely. Filesystem paths are also redacted to `[PATH]` — this
recognizes both Windows-style paths (`C:\...`, `\\unc\...`) and the original's Unix-style paths (for
data imported from a macOS install). Separate helpers (`RedactUrl`, `RedactBody`, `BodyPreview`) exist
for callers that explicitly log a URL or an HTTP response body, redacting a longer list of JSON keys
(tokens, emails, account/org/team IDs, etc.) — these must be called explicitly; they are not applied
automatically to every log line the way `RedactLogMessage` is.

## File size cap

**Not ported.** The original caps the log at ~10 MB with one rotated archive. `AppLog` in this port
appends indefinitely with no size limit or rotation — see [PORTING_NOTES.md](../PORTING_NOTES.md).
On a long-running tray app this file can grow without bound; delete it manually if it gets too large
(the app will recreate it on next launch).
