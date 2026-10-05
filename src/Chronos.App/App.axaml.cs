using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Chronos.App.Blocking;
using Chronos.App.Diagnostics;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Startup;
using Chronos.App.Time;
using Chronos.App.Tray;
using Chronos.App.ViewModels;
using Chronos.App.Views;

namespace Chronos.App;

public partial class App : Application
{
    private readonly AppLog _log;

    /// <summary>The constructor Avalonia tooling uses for the previewer; it keeps no log.</summary>
    public App()
        : this(AppLog.Silent)
    {
    }

    public App(AppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avalonia's default ends the process when the last window closes, so hiding the only
            // window would quit anyway.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var link = new ServiceLink(log: _log);

            // One clock reading for the whole interface.
            var clock = new SystemClock();

            // One switch and one Text for the process. It starts in the machine's language until
            // the configuration arrives with a choice.
            var language = new LanguageSwitch(_log, post: static action => Dispatcher.UIThread.Post(action));

            // The link publishes from its own loop; bound properties are touched on the interface thread.
            var ticker = new DispatcherTicker();

            var shell = new ShellViewModel(
                link,
                new Text(language),
                clock,
                ticker,
                quit: () => desktop.Shutdown(),
                post: static action => Dispatcher.UIThread.Post(action));

            var window = new MainWindow
            {
                DataContext = shell,
                Icon = Icon(),
            };

            desktop.MainWindow = window;

            var tray = new TrayController(shell.Tray, shell.Exit, () => Show(window));
            TrayIcon.SetIcons(this, [tray.Icon]);

            // Not part of the shell: a block is an interruption about a program, and two can be true at once.
            var notices = new BlockNotices(
                link,
                new BlockScreenRate(clock),
                block => Warn(shell.Text, block, clock, ticker),
                post: static action => Dispatcher.UIThread.Post(action));

            desktop.Exit += (_, _) =>
            {
                notices.Dispose();
                tray.Dispose();
                shell.Dispose();
                ticker.Dispose();
                link.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };

            RepairAutostart(_log);
            link.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>One window per block: the rate limit is per target, so two programs blocked together both show.</summary>
    private static void Warn(Text text, BlockEvent block, IClock clock, ITicker ticker) =>
        new BlockScreen
        {
            DataContext = new BlockViewModel(block, text, clock, ticker),
            Icon = Icon(),
        }.Show();

    /// <summary>The product's mark, for every window it opens.</summary>
    private static WindowIcon Icon() =>
        new(AssetLoader.Open(new Uri("avares://Chronos.App/Assets/chronos.ico")));

    /// <summary>Back from the notification area, wherever the window was left.</summary>
    private static void Show(Window window)
    {
        window.Show();

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>Writes a missing autostart entry and leaves an existing one alone, because the MSI owns its entry. A refusal is logged without the path.</summary>
    private static void RepairAutostart(AppLog log)
    {
        if (Autostart.ThisExecutable is not { } executable)
        {
            return;
        }

        try
        {
            new Autostart().Ensure(executable);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The user's own key is closed to the user. Blocking does not depend on it, but the log should say so.
            log.AutostartRefused();
        }
    }
}
