using Velopack;

namespace AIUsage.Tray;

/// <summary>
/// Custom entry point, replacing WPF's default App.xaml-generated Main(). Velopack recommends this
/// so VelopackApp.Build().Run() runs before any WPF startup overhead is paid — during an
/// install/update, Velopack re-invokes this executable with special hook arguments and may call
/// Environment.Exit() before ever constructing a Window, so nothing WPF-related should happen first.
/// App.xaml's Build Action was changed from ApplicationDefinition to Page to make this possible
/// (see AIUsage.Tray.csproj).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
