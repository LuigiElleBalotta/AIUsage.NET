using System.Windows;
using System.Windows.Forms;
using AIUsage.Core.App;

namespace AIUsage.Tray;

/// <summary>
/// Owns the tray icon and the metrics popup window. Simplified counterpart of the Swift
/// StatusItemController: a WinForms <see cref="NotifyIcon"/> (there is no first-party WPF tray-icon
/// API) standing in for NSStatusItem, and a plain borderless WPF window standing in for the
/// non-activating NSPanel. Omitted for now (see PORTING_NOTES.md): global keyboard shortcut,
/// screen-share privacy mode, transparency/vibrancy styling, panel height animation.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly AppContainer _container;
    private readonly NotifyIcon _notifyIcon;
    private readonly GlobalHotkeyService _hotkey;
    private MetricsWindow? _window;

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
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowPopup());
        menu.Items.Add("Refresh Now", null, async (_, _) => await _container.RefreshAllNowAsync().ConfigureAwait(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit AIUsage", null, (_, _) => Application.Current.Shutdown());
        return menu;
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
