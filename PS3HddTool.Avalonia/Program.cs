using Avalonia;

namespace PS3HddTool.Avalonia;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Linux: ask for the administrator password (polkit prompt) and run the app as root,
        // so physical drives can be listed and opened without starting it from a terminal.
        if (LinuxElevation.TryRelaunchElevated(args) is int elevatedExitCode)
            return elevatedExitCode;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
