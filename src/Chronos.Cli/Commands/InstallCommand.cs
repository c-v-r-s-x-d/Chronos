using Chronos.Service.Diagnostics;
using Chronos.Service.Setup;

namespace Chronos.Cli.Commands;

/// <summary>Puts the product on this machine.</summary>
/// <remarks>The MSI only lays files down and calls this, so service, task and directory setup live in one place.</remarks>
public static class InstallCommand
{
    /// <summary>Long enough for the service to load its configuration and open the filter engine; finite because nobody watches an MSI custom action.</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(SetupContext setup, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        ServiceRunState state;
        try
        {
            state = setup.Services.Query();
        }
        catch (Exception exception)
        {
            // Query throws for everything except NotInstalled. Installing over a service that may
            // exist could replace a working one with a half-registered one.
            error.WriteLine($"The service manager could not be asked about Chronos: {exception.Message}");

            return Steps.Failed;
        }

        // An existing service is updated, not refused: msiexec reads any non-zero exit as a rollback (error 1722).
        var registered = state is not ServiceRunState.NotInstalled;

        // Only without a service: where one is registered, cleanup could lift the block of a running session.
        var leftovers = !registered && await CleanFirstAsync(setup, output, error).ConfigureAwait(false);

        if (registered)
        {
            output.WriteLine(
                "Chronos is already registered on this machine. Nothing is being removed; what is "
                + "there is being brought in line with this build.");
        }

        // Order matters: ServiceLogging checks once at startup whether the event log source exists,
        // so register it before the service does.
        var steps = new (string Failure, Action Run)[]
        {
            ("The data directory could not be created", () =>
            {
                setup.CreateDataDirectory(setup.Paths.DataDirectory);

                output.WriteLine("The data directory is in place, writable only by SYSTEM and Administrators.");
            }),
            ("The Chronos event log source could not be registered", () =>
            {
                setup.EventLog.EnsureSource();
                output.WriteLine("The Chronos event log source is registered.");
            }),
            ServiceStep(setup, registered, output),
            ("The recovery task could not be registered", () =>
            {
                setup.Tasks.Register(RecoveryTaskDefinition.BuildXml(setup.CommandPath));
                output.WriteLine("The recovery task was registered, and runs at every boot.");
            }),
            ("The Chronos service could not be started", () =>
            {
                setup.Services.Start(StartTimeout);
                output.WriteLine("The Chronos service is running.");
            }),
        };

        foreach (var (failure, run) in steps)
        {
            if (!Steps.Run(failure, run, error))
            {
                // Unlike uninstall, stop at the first failure: each step is needed by the next.
                error.WriteLine("Chronos was not fully installed. Run 'chronos uninstall' and try again.");

                return Steps.Failed;
            }
        }

        // The MSI runs this without a console, so the event log is the record. Warning when the opening cleanup failed.
        setup.EventLog.Write(
            leftovers ? SystemEventLevel.Warning : SystemEventLevel.Information,
            ChronosEvents.Installed,
            leftovers
                ? "Chronos was installed, but what an earlier installation left behind could not be taken off."
                : registered
                    ? "Chronos was already registered on this machine and was brought in line with this build."
                    : "Chronos was installed.");

        output.WriteLine("Chronos is installed.");

        if (leftovers)
        {
            output.WriteLine(
                "Warning: what an earlier installation left on this machine could not be taken off. "
                + "It is named above, and 'chronos clean' can be run again to try once more.");
        }

        // Zero even when the opening cleanup failed: the MSI treats non-zero as a rollback, and
        // leftovers are no reason to undo a working installation.
        return Steps.Done;
    }

    /// <summary>Registers the service, or updates an existing one so every part of the definition matches this build.</summary>
    private static (string Failure, Action Run) ServiceStep(
        SetupContext setup, bool registered, TextWriter output) =>
        registered
            ? ("The Chronos service could not be brought in line with this build", () =>
            {
                setup.Services.Update(setup.Definition);
                output.WriteLine(
                    "The Chronos service was already registered, and now matches this build: its "
                    + "executable, its dependencies, its description, and the restarts it gets "
                    + "after a failure.");
            })
            : ("The Chronos service could not be registered", () =>
            {
                setup.Services.Install(setup.Definition);
                output.WriteLine("The Chronos service was registered, and will restart itself if it fails.");
            });

    /// <summary>Removes persistent filter objects left by older builds; returns true if that failed. Output is shown only when something was removed.</summary>
    private static async Task<bool> CleanFirstAsync(SetupContext setup, TextWriter output, TextWriter error)
    {
        CleanResult clean;
        var removed = new StringWriter();

        try
        {
            clean = await setup.Clean(removed, error).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error.WriteLine($"What an earlier installation left behind could not be taken off: {exception.Message}");

            return true;
        }

        if (clean.RemovedSomething)
        {
            output.WriteLine("What an earlier installation left on this machine was taken off first:");

            foreach (var line in removed.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                output.WriteLine($"  {line}");
            }
        }

        // Installation carries on; the caller turns this into a warning, not an exit code.
        return clean.ExitCode != 0;
    }
}
