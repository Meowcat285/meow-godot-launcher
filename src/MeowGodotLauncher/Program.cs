using Avalonia;
using Velopack;

namespace MeowGodotLauncher;

internal sealed class Program
{
    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code before AppMain
    // is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Handles the install, update and uninstall hooks of the Velopack packages, which exit right away, and does
        // nothing for builds that weren't installed from one.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
