using System.Net;
using System.Text.Json;
using AIUsage.Core.Services;

namespace AIUsage.Core.Services;

/// <summary>
/// Optional proxy routing for provider HTTP requests:
/// ~/.aiusage/config.json containing {"proxy": {"enabled": true, "url": "socks5://host:port"}}.
/// Loaded once at startup; restart the app after editing the file.
/// </summary>
public sealed class ProxyConfig
{
    public enum SchemeKind
    {
        Socks5,
        Http,
        Https
    }

    public SchemeKind Scheme { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    private static readonly string ConfigPath = Services.PathHelpers.ExpandHome("~/.aiusage/config.json");

    public static readonly ProxyConfig? Current = Load();

    private static ProxyConfig? Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var text = File.ReadAllText(ConfigPath);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("proxy", out var proxy)) return null;
            if (!proxy.TryGetProperty("enabled", out var enabledEl) || enabledEl.ValueKind != JsonValueKind.True) return null;
            if (!proxy.TryGetProperty("url", out var urlEl)) return null;
            var urlString = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(urlString)) return null;
            if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return null;

            var scheme = uri.Scheme.ToLowerInvariant() switch
            {
                "socks5" => SchemeKind.Socks5,
                "http" => SchemeKind.Http,
                "https" => SchemeKind.Https,
                _ => (SchemeKind?)null
            };
            if (scheme is null || string.IsNullOrEmpty(uri.Host)) return null;

            var defaultPort = scheme switch
            {
                SchemeKind.Socks5 => 1080,
                SchemeKind.Http => 80,
                SchemeKind.Https => 443,
                _ => 0
            };

            string? username = null, password = null;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                username = Uri.UnescapeDataString(parts[0]);
                password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
            }

            return new ProxyConfig
            {
                Scheme = scheme.Value,
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : defaultPort,
                Username = username,
                Password = password
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a WebProxy for HttpClientHandler. .NET's HttpClient/HttpClientHandler has no native
    /// SOCKS5 support, so a SOCKS5 config here degrades gracefully (documented limitation) — HTTP/HTTPS
    /// CONNECT proxies work fully.
    /// </summary>
    public IWebProxy ToWebProxy()
    {
        var proxyUri = new Uri($"http://{Host}:{Port}");
        var proxy = new WebProxy(proxyUri)
        {
            BypassList = new[] { "localhost", "127.0.0.1", "::1" }
        };
        if (Username is not null && Password is not null)
        {
            proxy.Credentials = new NetworkCredential(Username, Password);
        }
        return proxy;
    }
}
