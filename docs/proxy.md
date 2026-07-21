# Proxy

AIUsage.NET can route provider HTTP requests through an optional proxy.

- Supported fully: `http://`, `https://`
- Read but degraded: `socks5://` (see [Limitation](#limitation) below)
- Config file: `~/.aiusage/config.json` (resolves to `%USERPROFILE%\.aiusage\config.json`)
- Default: off
- UI: none — file only

## Config file

```json
{
  "proxy": {
    "enabled": true,
    "url": "http://127.0.0.1:8080"
  }
}
```

Authenticated proxies put credentials in the URL:

```json
{
  "proxy": {
    "enabled": true,
    "url": "http://user:pass@proxy.example.com:8080"
  }
}
```

When the URL has no port, the scheme's default applies (socks5 → 1080, http → 80, https → 443).

## Behavior

- The config is read once at process startup — **restart the tray app (or the CLI invocation) after
  changing the file**.
- `localhost`, `127.0.0.1`, and `::1` always bypass the proxy.
- A missing, disabled, invalid, or unreadable config simply leaves proxying off.

## Limitation

`System.Net.Http.HttpClientHandler` (the underlying .NET HTTP stack) has no native SOCKS5 support —
this is a platform limitation, not a design choice. A `socks5://` URL in the config is parsed and
accepted, but `ProxyConfig.ToWebProxy()` builds a plain `WebProxy` from it, which only works
correctly for HTTP/HTTPS. If you need SOCKS5, route through a local HTTP-to-SOCKS bridge, or use an
`http://`/`https://` proxy instead. See [PORTING_NOTES.md](../PORTING_NOTES.md) for the option of
adding a dedicated SOCKS5 library later.

## Scope

Applies to provider HTTP requests made by the app, including the hourly [model pricing](pricing.md)
refresh. It is not a system-wide proxy.
