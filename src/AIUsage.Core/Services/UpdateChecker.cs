using AIUsage.Core.Support;
using Velopack;
using Velopack.Sources;

namespace AIUsage.Core.Services;

/// <summary>Thin wrapper around <see cref="Velopack.UpdateManager"/> — the concrete Velopack type
/// isn't designed to be mocked directly in unit tests, so this narrows it down to the handful of
/// operations <see cref="UpdateChecker"/> needs, the same pattern as <see cref="IHttpClient"/> around
/// <c>HttpClient</c> or <see cref="ISqliteAccessing"/> around raw SQLite access elsewhere in this
/// codebase.</summary>
public interface IAppUpdateManager
{
    bool IsInstalled { get; }
    Task<UpdateInfo?> CheckForUpdatesAsync();
    Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress, CancellationToken cancellationToken);
    void ApplyUpdatesAndRestart(VelopackAsset? toApply);
}

/// <summary>Default <see cref="IAppUpdateManager"/> backed by a real <see cref="UpdateManager"/>
/// reading this repo's GitHub Releases via <see cref="GithubSource"/>.</summary>
public sealed class VelopackAppUpdateManager : IAppUpdateManager
{
    private const string RepoUrl = "https://github.com/LuigiElleBalotta/AIUsage.NET";
    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    public bool IsInstalled => _manager.IsInstalled;
    public Task<UpdateInfo?> CheckForUpdatesAsync() => _manager.CheckForUpdatesAsync();
    public Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync(updates, progress, cancellationToken);
    public void ApplyUpdatesAndRestart(VelopackAsset? toApply) => _manager.ApplyUpdatesAndRestart(toApply);
}

/// <summary>
/// Windows replacement for the Swift edition's Sparkle auto-update framework (see PORTING_NOTES.md).
/// Sparkle relies on a signed appcast XML feed plus an in-app installer step; Velopack is the closest
/// .NET equivalent (a maintained fork of Squirrel.Windows) and reads the update feed directly from
/// this repo's GitHub Releases — no separate feed/server to host.
///
/// Unlike the earlier GitHub-poll-only implementation, this one can actually download and apply an
/// update: <see cref="DownloadAndApplyAsync"/> fetches the new version and restarts the app into it.
/// The tray UI still asks the user before doing that (see TrayController) — nothing here installs
/// silently in the background.
/// </summary>
public sealed class UpdateChecker
{
    private const string ReleasesPageUrl = "https://github.com/LuigiElleBalotta/AIUsage.NET/releases/latest";
    private const string LastCheckKey = "aiusage.updateChecker.lastCheckedAt";
    private const string SkippedVersionKey = "aiusage.updateChecker.skippedVersion";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IAppUpdateManager _manager;
    private readonly ISettingsStore _settings;
    private readonly Func<DateTimeOffset> _now;

    public UpdateChecker(IAppUpdateManager? manager = null, ISettingsStore? settings = null, Func<DateTimeOffset>? now = null)
    {
        _manager = manager ?? new VelopackAppUpdateManager();
        _settings = settings ?? FileSettingsStore.Shared;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Result of a completed check. <c>LatestVersion</c> is null when the request failed
    /// (offline, rate-limited, not an installed build, etc.) — callers should treat that as "no
    /// update info available", never an error worth surfacing to the user.</summary>
    public sealed record Outcome(bool IsUpdateAvailable, string? LatestVersion, string? ReleaseUrl, UpdateInfo? UpdateInfo = null);

    /// <summary>Checks the GitHub release feed for a newer version than <paramref name="currentVersion"/>
    /// is only used for the "up to date" message text; Velopack compares against its own installed
    /// version internally. Does not consult or update the "last checked" throttle — use
    /// <see cref="CheckIfDueAsync"/> for the throttled, app-launch-time version of this call.</summary>
    public async Task<Outcome> CheckNowAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (!_manager.IsInstalled)
        {
            // A dev build run straight from bin/ or a portable copy from before the Velopack
            // switch — nothing Velopack can update in place. Not an error; just nothing to report.
            AppLog.Debug(LogTag.Updates, "update check skipped: not a Velopack-installed build");
            return new Outcome(false, null, null);
        }

        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                AppLog.Info(LogTag.Updates, $"up to date (current {currentVersion})");
                return new Outcome(false, currentVersion, null);
            }

            var latestVersion = info.TargetFullRelease.Version.ToString();
            AppLog.Info(LogTag.Updates, $"update available: {latestVersion} (current {currentVersion})");
            return new Outcome(true, latestVersion, ReleasesPageUrl, info);
        }
        catch (Exception ex)
        {
            AppLog.Debug(LogTag.Updates, $"release check failed: {ex.Message}");
            return new Outcome(false, null, null);
        }
    }

    /// <summary>Throttled variant meant to be called once per app launch: only hits the network if
    /// at least <see cref="CheckInterval"/> has passed since the last check, and suppresses a result
    /// the user already chose to skip via <see cref="SkipVersion"/>.</summary>
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

    /// <summary>Downloads the update described by <paramref name="outcome"/> and restarts the app
    /// into it. This exits the current process — callers should have already saved/closed anything
    /// that needs it. <paramref name="progress"/> is called with 0-100 during download.</summary>
    public async Task DownloadAndApplyAsync(Outcome outcome, Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (outcome.UpdateInfo is not { } info) throw new InvalidOperationException("Outcome has no downloadable update info.");
        AppLog.Info(LogTag.Updates, $"downloading update {outcome.LatestVersion}");
        await _manager.DownloadUpdatesAsync(info, progress, cancellationToken).ConfigureAwait(false);
        AppLog.Info(LogTag.Updates, $"applying update {outcome.LatestVersion} and restarting");
        _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    /// <summary>User chose "Skip this version" — future <see cref="CheckIfDueAsync"/> calls will not
    /// resurface this specific version (a newer one will still be reported).</summary>
    public void SkipVersion(string version) => _settings.SetString(SkippedVersionKey, version);
}
