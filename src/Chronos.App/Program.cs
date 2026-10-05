using Avalonia;
using Chronos.App.Diagnostics;

namespace Chronos.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Opened first, so a failure to come up at all can be recorded.
        using var log = AppLogging.Open(AppLogging.DefaultDirectory);

        // The two ways an exception gets past every handler. Saying nothing would leave a vanished
        // window and no explanation.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
            {
                log.Unhandled("the top of the process", error);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) => log.Unhandled("a task nobody awaited", e.Exception);

        log.Started();

        try
        {
            BuildAvaloniaApp(log).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error)
        {
            log.Unhandled("the interface loop", error);

            throw;
        }
        finally
        {
            log.Stopped();
        }
    }

    /// <summary>Shaped the way the Avalonia tooling expects, so it takes no log: a preview must not write into the real one.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(AppLog.Silent);

    private static AppBuilder BuildAvaloniaApp(AppLog log) =>
        AppBuilder.Configure(() => new App(log))
            .UsePlatformDetect();
}
