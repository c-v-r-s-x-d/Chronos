using System.Globalization;
using System.Text;
using System.Text.Json;
using Chronos.Cli.Commands;
using Chronos.Ipc;
using Chronos.Service.Sites;

namespace Chronos.Cli;

public static class Program
{
    private const string Usage =
        "Usage: chronos <status|start|extend|unlock|cancel|watch|clean|recover|diag|install|uninstall> "
        + "[--minutes N] [--purge] [--verbose]";

    /// <summary>Local time without spaces, so a double space stays a field boundary on an event line.</summary>
    private const string EventTime = "yyyy-MM-ddTHH:mm:ss";

    private const string VerboseFlag = "--verbose";

    private const string PurgeFlag = "--purge";

    private const string Unreadable = "The Chronos service sent an answer this program cannot read.";

    private const string ExitCodes =
        "Exit codes: 0 done, 1 rejected, 2 bad usage or not elevated, 3 service unreachable, "
        + "4 the machine could not be changed.";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            Console.Error.WriteLine(ExitCodes);
            return 2;
        }

        if (string.Equals(args[0], "watch", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1)
            {
                // Refused rather than ignored: extra arguments suggest a different command was meant.
                Console.Error.WriteLine("watch takes no arguments; it prints events until you stop it.");
                Console.Error.WriteLine(Usage);

                return 2;
            }

            return await WatchAsync(new IpcClient(), Console.Out, Console.Error);
        }

        if (string.Equals(args[0], "clean", StringComparison.OrdinalIgnoreCase))
        {
            // Runs without the service: it exists for a service that will not start. It still needs
            // admin rights; unelevated it would print a success line for the hosts file (readable
            // by everyone) and then fail on the filters.
            return await CleanAsync(
                Administrator.IsElevated,
                () => CleanCommand.RunAsync(new HostsFile(HostsFile.SystemPath), Console.Out, Console.Error),
                Console.Error);
        }

        if (string.Equals(args[0], "recover", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1)
            {
                Console.Error.WriteLine("recover takes no arguments.");
                Console.Error.WriteLine(Usage);

                return 2;
            }

            if (!Administrator.IsElevated())
            {
                return Administrator.Refuse("recover", Console.Error);
            }

            return ThisMachine(SetupContext.ForThisMachine, Console.Error) is { } machine
                ? await RecoverCommand.RunAsync(machine, Console.Out, Console.Error, CancellationToken.None)
                : Steps.Failed;
        }

        if (string.Equals(args[0], "diag", StringComparison.OrdinalIgnoreCase))
        {
            return await DiagAsync(
                args,
                Administrator.IsElevated,
                SetupContext.ForThisMachine,
                Console.Out,
                Console.Error,
                CancellationToken.None);
        }

        if (string.Equals(args[0], "install", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1)
            {
                // Catches --purge typed on the wrong command.
                Console.Error.WriteLine("install takes no arguments.");
                Console.Error.WriteLine(Usage);

                return 2;
            }

            if (!Administrator.IsElevated())
            {
                return Administrator.Refuse("install", Console.Error);
            }

            return ThisMachine(SetupContext.ForThisMachine, Console.Error) is { } machine
                ? await InstallCommand.RunAsync(machine, Console.Out, Console.Error)
                : Steps.Failed;
        }

        if (string.Equals(args[0], "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            // --purge deletes the user's lists, so any other argument stops the run.
            if (!TryReadFlag(args, PurgeFlag, out var purge))
            {
                Console.Error.WriteLine("uninstall takes no arguments but --purge.");
                Console.Error.WriteLine(Usage);

                return 2;
            }

            if (!Administrator.IsElevated())
            {
                return Administrator.Refuse("uninstall", Console.Error);
            }

            return ThisMachine(SetupContext.ForThisMachine, Console.Error) is { } machine
                ? await UninstallCommand.RunAsync(machine, purge, Console.Out, Console.Error)
                : Steps.Failed;
        }

        var command = MapCommand(args[0]);
        if (command is null)
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            Console.Error.WriteLine(Usage);
            Console.Error.WriteLine(ExitCodes);
            return 2;
        }

        if (!TryReadMinutes(args, out var minutes, out var minutesError))
        {
            Console.Error.WriteLine(minutesError);
            return 2;
        }

        return await ClientCommandAsync(
            new IpcClient(),
            new IpcRequest { Command = command, DurationMinutes = minutes },
            Console.Out,
            Console.Error);
    }

    /// <summary>Sends one request and prints the answer. Separate from <see cref="Main"/> so tests can supply a pipe.</summary>
    internal static async Task<int> ClientCommandAsync(
        IpcClient client, IpcRequest request, TextWriter output, TextWriter error)
    {
        try
        {
            var response = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);

            if (!AnswerCheck.IsUsable(response, wantsConfig: request.Command == "GetConfig"))
            {
                error.WriteLine(Unreadable);

                return 3;
            }

            Print(response, output, error);
            return response.Accepted ? 0 : 1;
        }
        catch (JsonException)
        {
            // A line arrived but is not an answer: a service dying mid-write, another process on the pipe, or a JSON null.
            error.WriteLine(Unreadable);
            return 3;
        }
        catch (Exception exception)
            when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            error.WriteLine("Chronos service is not reachable. Is it running?");
            return 3;
        }
    }

    /// <summary>The <c>diag</c> command line, with the rights check and machine supplied so tests can drive it.</summary>
    /// <remarks>
    /// <c>--verbose</c> adds blocked addresses, the hosts block and every rule to the package, so it must be read strictly.
    /// </remarks>
    internal static async Task<int> DiagAsync(
        string[] args,
        Func<bool> elevated,
        Func<SetupContext> machine,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (!TryReadFlag(args, VerboseFlag, out var verbose))
        {
            error.WriteLine("diag takes no arguments but --verbose.");
            error.WriteLine(Usage);

            return 2;
        }

        // The filter engine needs admin rights. The machine is a delegate so nothing is read on a refused run.
        if (!elevated())
        {
            return Administrator.Refuse("diag", error);
        }

        return ThisMachine(machine, error) is { } described
            ? await DiagCommand.RunAsync(described, verbose, output, error, ct).ConfigureAwait(false)
            : Steps.Failed;
    }

    /// <summary>The machine as the setup commands see it, or null after a message when <see cref="SetupContext.ForThisMachine()"/> cannot tell where this process runs from.</summary>
    internal static SetupContext? ThisMachine(Func<SetupContext> machine, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return machine();
        }
        catch (InvalidOperationException exception)
        {
            error.WriteLine($"Chronos cannot tell what machine it is on: {exception.Message}");
            error.WriteLine(
                "Where the service and the recovery task live is worked out from the path this "
                + "program was started from, and this host reported none.");

            return null;
        }
    }

    /// <summary><c>clean</c> with the rights check and cleanup supplied, so tests can verify a refusal never runs the cleanup.</summary>
    internal static Task<int> CleanAsync(Func<bool> elevated, Func<Task<int>> clean, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(elevated);
        ArgumentNullException.ThrowIfNull(clean);
        ArgumentNullException.ThrowIfNull(error);

        return elevated() ? clean() : Task.FromResult(Administrator.Refuse("clean", error));
    }

    /// <summary>True when everything after the verb is the one optional <paramref name="flag"/>, given at most once; <paramref name="present"/> says whether it was there.</summary>
    /// <remarks>Ordinal, so <c>--VERBOSE</c> is refused rather than guessed at.</remarks>
    internal static bool TryReadFlag(string[] args, string flag, out bool present)
    {
        ArgumentNullException.ThrowIfNull(args);

        present = false;

        for (var i = 1; i < args.Length; i++)
        {
            // A repeated flag is refused like any other stray argument.
            if (present || !string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                // A rejected line must not leave the flag set.
                present = false;

                return false;
            }

            present = true;
        }

        return true;
    }

    /// <summary>Follows the service's event stream until Ctrl+C, which is handled so no stack trace follows.</summary>
    public static async Task<int> WatchAsync(IpcClient client, TextWriter output, TextWriter error)
    {
        using var stopping = new CancellationTokenSource();

        void Interrupt(object? sender, ConsoleCancelEventArgs interrupted)
        {
            interrupted.Cancel = true;
            stopping.Cancel();
        }

        Console.CancelKeyPress += Interrupt;

        try
        {
            return await WatchAsync(client, output, error, stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            // Unhook before the source is disposed.
            Console.CancelKeyPress -= Interrupt;
        }
    }

    /// <summary>The watch with the cancellation supplied.</summary>
    public static async Task<int> WatchAsync(
        IpcClient client, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        IpcSubscription subscription;
        try
        {
            subscription = await client.SubscribeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Interrupted while connecting.
            return 0;
        }
        catch (JsonException)
        {
            error.WriteLine(Unreadable);

            return 3;
        }
        catch (Exception exception)
            when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            error.WriteLine("Chronos service is not reachable. Is it running?");

            return 3;
        }

        await using (subscription.ConfigureAwait(false))
        {
            if (!subscription.Acknowledgement.Accepted)
            {
                error.WriteLine(subscription.Acknowledgement.Error is { } code ? ServiceText.English(code) : "The subscription was refused.");

                return 1;
            }

            if (!AnswerCheck.IsUsable(subscription.Acknowledgement))
            {
                error.WriteLine(Unreadable);

                return 3;
            }

            output.WriteLine("Watching the Chronos service. Press Ctrl+C to stop.");

            // The acknowledgement carries the starting status.
            Print(subscription.Acknowledgement, output, error);

            try
            {
                await foreach (var published in subscription.ReadEventsAsync(ct).ConfigureAwait(false))
                {
                    PrintEvent(published, output);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return 0;
            }

            // The stream ended on its own, so this is not a success.
            error.WriteLine("The Chronos service closed the event stream. Is it still running?");

            return 3;
        }
    }

    /// <summary>The service command a verb sends, or null for an unknown verb. Public so tests send what the CLI would.</summary>
    public static string? MapCommand(string verb) => verb?.ToLowerInvariant() switch
    {
        // GetConfig answers with the status too, and shows the configuration of an idle machine.
        "status" => "GetConfig",
        "start" => "StartSession",
        "extend" => "ExtendSession",
        "unlock" => "RequestUnlock",
        "cancel" => "CancelUnlock",
        _ => null,
    };

    private static bool TryReadMinutes(string[] args, out int? minutes, out string? error)
    {
        minutes = null;
        error = null;

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is not "--minutes")
            {
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = "--minutes needs a value, for example: chronos start --minutes 90";
                return false;
            }

            if (!int.TryParse(args[i + 1], out var parsed))
            {
                error = $"'{args[i + 1]}' is not a number of minutes.";
                return false;
            }

            minutes = parsed;
            return true;
        }

        return true;
    }

    public static void Print(IpcResponse response, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (response.Status is { } status)
        {
            output.WriteLine($"State:  {status.State}");
            output.WriteLine($"Ends:   {status.EndsAt?.ToLocalTime().ToString("g") ?? "-"}");
            output.WriteLine($"Unlock: {status.UnlockEffectiveAt?.ToLocalTime().ToString("g") ?? "-"}");
            // Only during a session: the snapshot belongs to it, so idle counts would read as "no rules".
            if (status.StartedAt is not null)
            {
                output.WriteLine($"Locked: {status.Sites.Count} sites, {status.Apps.Count} apps");
            }

            if (response.Config is { } configured)
            {
                // What the next session will block; a rule removed mid-session stays in force until it ends.
                output.WriteLine($"Config: {configured.Sites.Count} sites, {configured.Apps.Count} apps");
            }

            PrintLayers(status, output);
        }

        foreach (var warning in response.Warnings ?? [])
        {
            output.WriteLine($"warning: {ServiceText.English(warning)}");
        }

        if (!response.Accepted)
        {
            error.WriteLine(response.Error is { } code ? ServiceText.English(code) : "Command rejected.");
        }
    }

    public static void PrintEvent(IpcEvent published, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(output);

        // The kind leads and is padded so the column stays straight.
        var line = new StringBuilder()
            .Append(published.Kind.PadRight(IpcEventKind.StatusChanged.Length))
            .Append("  ")
            .Append(published.At.ToLocalTime().ToString(EventTime, CultureInfo.InvariantCulture));

        if (published.Status is { } status)
        {
            // Counts and rule lists stay out: a rule can be a full path, which is logged only at Debug.
            line.Append("  state=").Append(status.State).Append("  ends=").Append(Moment(status.EndsAt));

            if (status.UnlockEffectiveAt is { } unlock)
            {
                line.Append("  unlock=").Append(Moment(unlock));
            }
        }
        else
        {
            line.Append("  (no status)");
        }

        if (published.AppName is { Length: > 0 } appName)
        {
            // A file name from the user's own list is fine to print; a path is not.
            line.Append("  app=").Append(appName);
        }

        output.WriteLine(line.ToString());
    }

    private static string Moment(DateTimeOffset? at) =>
        at?.ToLocalTime().ToString(EventTime, CultureInfo.InvariantCulture) ?? "-";

    private static void PrintLayers(StatusPayload status, TextWriter output)
    {
        if (status.Layers.Count == 0)
        {
            output.WriteLine("Layers: none reported yet");

            return;
        }

        output.WriteLine("Layers:");

        var width = status.Layers.Max(layer => layer.Name.Length);
        foreach (var layer in status.Layers)
        {
            var reason = layer.IsAvailable ? "available" : $"unavailable: {layer.ReasonCode ?? "no reason given"}";
            output.WriteLine($"  {layer.Name.PadRight(width)}  {reason}");
        }
    }
}
