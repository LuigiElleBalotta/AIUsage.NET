using System.IO;
using System.Windows;
using AIUsage.Core.App;
using AIUsage.Core.Support;

namespace AIUsage.Tray;

/// <summary>
/// Interaction logic for App.xaml. Composition root entry point: owns the AppContainer for the
/// process lifetime and the tray/status-item controller. Direct counterpart of the Swift
/// OpenUsageApp + AppDelegate pairing, minus Sparkle updates and the local HTTP API (not yet ported).
/// </summary>
public partial class App : Application
{
    private AppContainer? _container;
    private TrayController? _trayController;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsage", "Logs", "AIUsage.log");
        AppLog.Bootstrap(logPath);

        var isFreshInstall = !File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsage", "settings.json"));

        _container = new AppContainer(isFreshInstall);
        _trayController = new TrayController(_container);

        // Dev convenience: `AIUsage.exe --show` opens the dashboard immediately instead of waiting
        // for a tray-icon click, useful for quick visual checks during development.
        if (e.Args.Contains("--show"))
        {
            _trayController.ShowForDebug();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayController?.Dispose();
        _container?.Dispose();
        base.OnExit(e);
    }
}
