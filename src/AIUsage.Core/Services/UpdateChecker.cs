using System.Text.Json;
using AIUsage.Core.Support;

namespace AIUsage.Core.Services;

/// <summary>
/// Windows replacement for the Swift edition's Sparkle auto-update framework (see PORTING_NOTES.md).
/// Sparkle relies on a signed appcast XML feed plus an in-app installer step — neither has a direct
/// .NET equivalent worth building for a single-maintainer OSS project, so this is deliberately the
/// simple alternative called out in the porting notes: poll the GitHub Releases API for the latest
/// tag, compare it against the running version, and if newer, surface a "download" link that opens
/// the release page in the user's browser. No silent/automatic install — the user always chooses to
/// download and run the new installer themselves.
/// </summary>
public sealed class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/LuigiElleBalotta/AIUsage.NET/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/LuigiElleBalotta/AIUsage.NET/releases/latest";
    private const string LastCheckKey = "aiusage.updateChecker.lastCheckedAt";
    private const string SkippedVersionKey = "aiusage.updateChecker.skippedVersion";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IHttpClient _http;
    private readonly ISettingsStore _settings;
    private readonly Func<DateTimeOffset> _now;

    public UpdateChecker(IHttpClient? http = null, ISettingsStore? settings = null, Func<DateTimeOffset>? now = null)
    {
        _http = http ?? new SystemHttpClient();
        _settings = settings ?? FileSettingsStore.Shared;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Result of a completed check. <c>Latest</c> is null when the request failed (offline,
    /// rate-limited, etc.) — callers should treat that as "no update info available", never an error
    /// worth surfacing to the user.</summary>
    public sealed record Outcome(bool IsUpdateAvailable, string? LatestVersion, string? ReleaseUrl);

    /// <summary>Fetches the latest GitHub release and compares it against <paramref name="currentVersion"/>
    /// (e.g. from AppVersion.Display()). Does not consult or update the "last checked" throttle —
    /// use <see cref="CheckIfDueAsync"/> for the throttled, app-launch-time version of this call.</summary>
    public async Task<Outcome> CheckNowAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.SendAsync(new HttpRequestSpec
            {
                Method = "GET",
                Url = new Uri(ApiUrl),
                Headers = new Dictionary<string, string>
                {
                    ["Accept"] = "application/vnd.github+json",
                    ["User-Agent"] = "AIUsage.NET-update-checker"
                },
                Timeout = TimeSpan.FromSeconds(10)
            }, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != 200)
            {
                AppLog.Debug(LogTag.Updates, $"release check returned status {response.StatusCode}");
                return new Outcome(false, null, null);
            }

            using var doc = JsonDocument.Parse(response.Body);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            var htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : ReleasesPageUrl;
            if (string.IsNullOrWhiteSpace(tag)) return new Outcome(false, null, null);

            var latestVersion = tag.StartsWith('v') ? tag[1..] : tag;
            var isNewer = IsNewer(latestVersion, currentVersion);
            AppLog.Info(LogTag.Updates, isNewer
                ? $"update available: {latestVersion} (current {currentVersion})"
                : $"up to date (latest {latestVersion}, current {currentVersion})");

            return new Outcome(isNewer, latestVersion, htmlUrl ?? ReleasesPageUrl);
        }
        catch (Exception ex)
        {
            AppLog.Debug(LogTag.Updates, $"release check failed: {ex.Message}");
            return new Outcome(false, null, null);
        }
    }

    /// <summary>Throttled variant meant to be called once per app launch / periodically: only hits
    /// the network if at least <see cref="CheckInterval"/> has passed since the last check, and
    /// suppresses a result the user already chose to skip via <see cref="SkipVersion"/>.</summary>
    public async Task<Outcome?> CheckIfDueAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var lastCheckRaw = _settings.GetString(LastCheckKey);
        if (lastCheckRaw is not null && DateTimeOffset.TryParse(lastCheckRaw, out var lastCheck))
        {
            if (_now() - lastCheck < CheckInterval) return null;
        }

        var outcome = await CheckNowAsync(currentVersion, cancellationToken).ConfigureAwait(false);
        _settings.SetString(LastCheckKey, _now().ToString("O"));

        if (outcome.IsUpdateAvailable && outcome.LatestVersion == _settings.GetString(SkippedVersionKey))
        {
            return outcome with { IsUpdateAvailable = false };
        }

        return outcome;
    }

    /// <summary>User chose "Skip this version" — future <see cref="CheckIfDueAsync"/> calls will not
    /// resurface this specific version (a newer one will still be reported).</summary>
    public void SkipVersion(string version) => _settings.SetString(SkippedVersionKey, version);

    /// <summary>Compares two dotted-numeric version strings (e.g. "0.2.0" vs "0.1.2"), ignoring any
    /// pre-release/build suffix after a "-" or "+". Missing/non-numeric components are treated as 0,
    /// so "0.2" is newer than "0.1.9" and "0.2.0" is treated equal to "0.2".</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var candidateParts = ParseVersion(candidate);
        var currentParts = ParseVersion(current);
        var length = Math.Max(candidateParts.Length, currentParts.Length);
        for (var i = 0; i < length; i++)
        {
            var c = i < candidateParts.Length ? candidateParts[i] : 0;
            var u = i < currentParts.Length ? currentParts[i] : 0;
            if (c != u) return c > u;
        }
        return false;
    }

    private static int[] ParseVersion(string version)
    {
        var core = version.Split('-', '+')[0];
        return core.Split('.')
            .Select(part => int.TryParse(part, out var n) ? n : 0)
            .ToArray();
    }
}
