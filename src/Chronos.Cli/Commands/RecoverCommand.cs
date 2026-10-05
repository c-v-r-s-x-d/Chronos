using Chronos.Core.Time;
using Chronos.Service.Diagnostics;
using Chronos.Service.Setup;

namespace Chronos.Cli.Commands;

/// <summary>Boot-time recovery: if the service does not answer within the window, take every change off the machine.</summary>
/// <remarks>
/// Working means a service that answers, not one the manager reports as running. The window is a
/// ceiling: the command ends as soon as the service answers.
/// </remarks>
public static class RecoverCommand
{
    /// <summary>Total time allowed, wait plus cleanup.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>The part of <see cref="Window"/> kept for the cleanup, so the whole run fits in it.</summary>
    private static readonly TimeSpan CleanupReserve = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    public static Task<int> RunAsync(SetupContext setup, TextWriter output, TextWriter error, CancellationToken ct) =>
        RunAsync(setup, SystemClock.Instance, (span, token) => Task.Delay(span, token), output, error, ct);

    /// <summary>The command with the clock and the wait supplied.</summary>
    public static async Task<int> RunAsync(
        SetupContext setup,
        IClock clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (await WaitForAWorkingServiceAsync(setup, clock, delay, output, error, ct).ConfigureAwait(false))
        {
            output.WriteLine("The Chronos service is running and answering. Nothing on this machine was changed.");

            return Steps.Done;
        }

        return ClearTheMachine(setup, await CleanAsync(setup, output, error).ConfigureAwait(false), output, error);
    }

    /// <summary>Waits up to <see cref="Window"/> less <see cref="CleanupReserve"/> for a service that answers.</summary>
    private static async Task<bool> WaitForAWorkingServiceAsync(
        SetupContext setup,
        IClock clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        var deadline = clock.UtcNow + Window - CleanupReserve;
        var started = false;
        var reportedTheManager = false;

        // Null when the manager could not be asked, which is not a reason to stop waiting. Reported once.
        ServiceRunState? Query()
        {
            try
            {
                return setup.Services.Query();
            }
            catch (Exception exception)
            {
                if (!reportedTheManager)
                {
                    error.WriteLine($"The service manager could not be asked about Chronos: {exception.Message}");
                    reportedTheManager = true;
                }

                return null;
            }
        }

        while (true)
        {
            // Check before the round trip: a query at the deadline would eat into the cleanup time.
            var remaining = deadline - clock.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                error.WriteLine("The Chronos service did not reach a working state within two minutes.");

                return false;
            }

            var state = Query();

            // Both: the manager says it runs and the service answers.
            if (state is ServiceRunState.Running && await setup.AnswersAsync(ct).ConfigureAwait(false))
            {
                return true;
            }

            if (state is ServiceRunState.NotInstalled)
            {
                // Nothing will start an unregistered service, so do not wait.
                error.WriteLine("The Chronos service is not registered on this machine.");

                return false;
            }

            if (state is ServiceRunState.Stopped && !started)
            {
                // Try one start. A stopped service has nobody to restart it; the start gets only the time that is left.
                started = true;
                Start(setup, remaining, output, error);

                continue;
            }

            // Re-read the clock: the query and the pipe probe take time, and the wait must not overshoot the deadline.
            var left = deadline - clock.UtcNow;
            if (left > TimeSpan.Zero)
            {
                await delay(left < Poll ? left : Poll, ct).ConfigureAwait(false);
            }
        }
    }

    private static void Start(SetupContext setup, TimeSpan timeout, TextWriter output, TextWriter error)
    {
        try
        {
            setup.Services.Start(timeout);
            output.WriteLine("The Chronos service was stopped, and was started.");
        }
        catch (Exception exception) when (ServiceManagerRace.LostToTheManager(exception))
        {
            // The service fell over and the manager is restarting it. Keep waiting; do not clean up.
            output.WriteLine("The service manager is already starting Chronos. Waiting for it.");
        }
        catch (Exception exception)
        {
            // The start was only a nudge; keep waiting.
            error.WriteLine($"The Chronos service could not be started: {exception.Message}");
        }
    }

    private static async Task<int> CleanAsync(SetupContext setup, TextWriter output, TextWriter error)
    {
        output.WriteLine("Taking every change Chronos made to this machine back off it.");

        try
        {
            return (await setup.Clean(output, error).ConfigureAwait(false)).ExitCode;
        }
        catch (Exception exception)
        {
            // Catches everything: nobody reads a stack trace from a boot-time task.
            error.WriteLine($"The changes Chronos made to this machine could not be taken off: {exception.Message}");

            return Steps.Failed;
        }
    }

    /// <summary>Clears the saved state and writes the event log entry. DNS was already restored by the cleanup.</summary>
    private static int ClearTheMachine(SetupContext setup, int cleaned, TextWriter output, TextWriter error)
    {
        // Always, even if a session has hours left and whatever the cleanup managed.
        var cleared = Steps.Run(
            "The session state could not be cleared",
            () =>
            {
                setup.ClearState();
                output.WriteLine("The saved session was cleared, however much of it was left to run.");
            },
            error);

        var failed = cleaned != Steps.Done || !cleared;

        // One Error entry, only on this path. The healthy path writes nothing: the task runs at every boot.
        setup.EventLog.Write(
            SystemEventLevel.Error,
            failed ? ChronosEvents.RecoveryFailed : ChronosEvents.RecoveryCleared,
            failed
                ? "Chronos did not reach a working state, and not all of what it changed could be taken off."
                : "Chronos did not reach a working state, so everything it changed was taken off this machine.");

        output.WriteLine(failed
            ? "Chronos was shut down, but not all of it came off. What is left is named above."
            : "This machine is back to how it was before Chronos ran.");

        return failed ? Steps.Failed : Steps.Done;
    }
}
