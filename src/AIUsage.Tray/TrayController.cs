using System.Windows;
using System.Windows.Forms;
using AIUsage.Core.App;
using AIUsage.Core.Services;

namespace AIUsage.Tray;

/// <summary>
/// Owns the tray icon and the metrics popup window. Simplified counterpart of the Swift
/// StatusItemController: a WinForms <see cref="NotifyIcon"/> (there is no first-party WPF tray-icon
/// API) standing in for NSStatusItem, and a plain borderless WPF window standing in for the
/// non-activating NSPanel. Omitted for now (see PORTING_NOTES.md): screen-share privacy mode,
/// transparency/vibrancy styling, panel height animation.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly AppContainer _container;
    private readonly NotifyIcon _notifyIcon;
    private readonly GlobalHotkeyService _hotkey;
    private readonly UpdateChecker _updateChecker = new();
    private MetricsWindow? _window;
    private ToolStripMenuItem? _updateMenuItem;
    private UpdateChecker.Outcome? _pendingUpdate;

    public TrayController(AppContainer container)
    {
        _container = container;

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "AIUsage",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePopup();
        };

        _container.SnapshotsChanged += OnSnapshotsChanged;

        _hotkey = new GlobalHotkeyService(() => Application.Current?.Dispatcher.Invoke(TogglePopup));
        _hotkey.Start();

        _ = CheckForUpdatesOnLaunchAsync();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowPopup());
        menu.Items.Add("Refresh Now", null, async (_, _) => await _container.RefreshAllNowAsync().ConfigureAwait(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("Check for Updates...", null, async (_, _) => await CheckForUpdatesManuallyAsync().ConfigureAwait(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit AIUsage", null, (_, _) => Application.Current.Shutdown());
        return menu;
    }

    /// <summary>Silent, throttled check run once per launch (see PORTING_NOTES.md — this is the
    /// Windows replacement for Sparkle: no auto-download/install, just a GitHub Releases poll that
    /// surfaces a tray balloon + a persistent "Update available" menu item when one is found.</summary>
    private async Task CheckForUpdatesOnLaunchAsync()
    {
        var outcome = await _updateChecker.CheckIfDueAsync(AppVersion.Display()).ConfigureAwait(true);
        if (outcome is { IsUpdateAvailable: true }) ShowUpdateAvailable(outcome);
    }

    private async Task CheckForUpdatesManuallyAsync()
    {
        var outcome = await _updateChecker.CheckNowAsync(AppVersion.Display()).ConfigureAwait(true);
        if (outcome.IsUpdateAvailable)
        {
            ShowUpdateAvailable(outcome);
        }
        else if (outcome.LatestVersion is not null)
        {
            _notifyIcon.ShowBalloonTip(4000, "AIUsage", $"You're up to date (v{AppVersion.Display()}).", ToolTipIcon.Info);
        }
        else
        {
            _notifyIcon.ShowBalloonTip(4000, "AIUsage", "Couldn't check for updates right now.", ToolTipIcon.Warning);
        }
    }

    private void ShowUpdateAvailable(UpdateChecker.Outcome outcome)
    {
        _pendingUpdate = outcome;
        if (_updateMenuItem is null)
        {
            _updateMenuItem = new ToolStripMenuItem { Font = new System.Drawing.Font(_notifyIcon.ContextMenuStrip!.Font, System.Drawing.FontStyle.Bold) };
            _updateMenuItem.Click += async (_, _) => await InstallPendingUpdateAsync().ConfigureAwait(true);
            _notifyIcon.ContextMenuStrip!.Items.Insert(0, _updateMenuItem);
            _notifyIcon.ContextMenuStrip!.Items.Insert(1, new ToolStripSeparator());
        }
        _updateMenuItem.Text = $"Install update: v{outcome.LatestVersion}";
        _notifyIcon.ShowBalloonTip(6000, "AIUsage update available",
            $"Version {outcome.LatestVersion} is available. Click the tray icon menu to install and restart.", ToolTipIcon.Info);
    }

    /// <summary>Downloads and applies the pending update, then restarts the app. This is the only
    /// path that actually installs anything — nothing runs silently in the background; the user must
    /// click the tray menu item first.</summary>
    private async Task InstallPendingUpdateAsync()
    {
        if (_pendingUpdate is not { } outcome || outcome.UpdateInfo is null) return;
        try
        {
            _notifyIcon.ShowBalloonTip(4000, "AIUsage", $"Downloading v{outcome.LatestVersion}...", ToolTipIcon.Info);
            await _updateChecker.DownloadAndApplyAsync(outcome).ConfigureAwait(true);
            // ApplyUpdatesAndRestart exits this process; execution normally does not continue past here.
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(6000, "AIUsage", $"Update failed: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void OnSnapshotsChanged()
    {
        Application.Current?.Dispatcher.Invoke(() => _window?.Refresh());
    }

    private void TogglePopup()
    {
        if (_window is { IsVisible: true })
        {
            _window.Hide();
        }
        else
        {
            ShowPopup();
        }
    }

    private void ShowPopup()
    {
        _window ??= new MetricsWindow(_container);
        _window.Refresh();
        _window.PositionNearTray();
        _window.Show();
        _window.Activate();
    }

    private void ShowSettings()
    {
        var settings = new SettingsWindow(_container);
        settings.Closed += (_, _) => _window?.Refresh();
        settings.ShowDialog();
    }

    /// <summary>Dev-only helper for `--show`: opens the dashboard as a normal top-level window
    /// (not tray-anchored) so it can be screenshotted without needing a tray-icon click.</summary>
    public void ShowForDebug()
    {
        _window ??= new MetricsWindow(_container);
        _window.Refresh();
        _window.Left = 100;
        _window.Top = 100;
        _window.Show();
        _window.Activate();
    }

    public void Dispose()
    {
        _container.SnapshotsChanged -= OnSnapshotsChanged;
        _hotkey.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _window?.Close();
    }
}
