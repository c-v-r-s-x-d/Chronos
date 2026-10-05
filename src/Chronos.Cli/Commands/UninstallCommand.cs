using System.Text;
using Chronos.Service.Diagnostics;
using Chronos.Service.Dns;
using Chronos.Service.Setup;

namespace Chronos.Cli.Commands;

/// <summary>Takes the product, and everything it changed, back off this machine.</summary>
/// <remarks>
/// The block comes off before the service stops, or its next pass would put the filters back. A
/// failed step does not end the run. Configuration and logs stay unless <c>--purge</c>; the saved
/// session is always cleared.
/// </remarks>
public static class UninstallCommand
{
    /// <summary>How long a stop is given before it counts as failed.</summary>
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(SetupContext setup, bool purge, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // Clean first, while the service that would reapply the block is still running. The
        // recording writer keeps the cleanup's errors so the second DNS restore does not repeat them.
        var errors = new RecordingWriter(error);
        var failed = !await CleanFirstAsync(setup, output, errors).ConfigureAwait(false);

        failed |= !StopAndRemoveService(setup, output, error);
        failed |= !RestoreDnsAgain(setup, output, error, errors.Written);
        failed |= !RemoveProductKey(setup, output, error);
        failed |= !ClearSessionState(setup, output, error);
        failed |= !RemoveTask(setup, output, error);
        failed |= !RemoveAutostart(setup, output, error);

        // Before the source is unregistered. The MSI runs this without a console, so this entry is the record.
        setup.EventLog.Write(
            failed ? SystemEventLevel.Warning : SystemEventLevel.Information,
            ChronosEvents.Uninstalled,
            failed
                ? "Chronos was uninstalled, but some of it could not be removed."
                : "Chronos was uninstalled.");

        failed |= !Steps.Run(
            "The Chronos event log source could not be removed",
            () =>
            {
                setup.EventLog.RemoveSource();
                output.WriteLine("The Chronos event log source was removed.");
            },
            error);

        failed |= !RemoveData(setup, purge, output, error);

        output.WriteLine(failed
            ? "Chronos was removed, but not all of it. What is left is named above."
            : "Chronos was removed.");

        return failed ? Steps.Failed : Steps.Done;
    }

    private static async Task<bool> CleanFirstAsync(SetupContext setup, TextWriter output, TextWriter error)
    {
        var said = new StringWriter();
        try
        {
            return (await setup.Clean(said, error).ConfigureAwait(false)).ExitCode == 0;
        }
        catch (Exception exception)
        {
            error.WriteLine($"The changes Chronos made to this machine could not be taken off: {exception.Message}");

            return false;
        }
        finally
        {
            // "For when they return" is untrue once the product is gone; the second restore points at the manual fallback.
            foreach (var line in said.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.EndsWith(CleanCommand.AbsentStays, StringComparison.Ordinal))
                {
                    output.WriteLine(line);
                }
            }
        }
    }

    /// <summary>Restores DNS again after the service stopped, since it may have re-taken the interfaces. Reports only when something went back.</summary>
    private static bool RestoreDnsAgain(SetupContext setup, TextWriter output, TextWriter error, string alreadySaid)
    {
        var said = new StringWriter();
        var complaints = new StringWriter();
        var result = setup.RestoreDns(said, complaints);

        foreach (var line in complaints.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!alreadySaid.Contains(line, StringComparison.Ordinal))
            {
                error.WriteLine(line);
            }
        }

        if (result.RemovedSomething)
        {
            output.WriteLine("Put back once more after the service stopped:");

            foreach (var line in said.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                output.WriteLine($"  {line}");
            }
        }

        return result.ExitCode == 0;
    }

    /// <summary>Removes HKLM\SOFTWARE\Chronos with or without <c>--purge</c>. Runs after the second restore so a kept backup keeps the key.</summary>
    private static bool RemoveProductKey(SetupContext setup, TextWriter output, TextWriter error) => Steps.Run(
        @"The registry key HKLM\SOFTWARE\Chronos could not be removed",
        () =>
        {
            switch (setup.RemoveProductKey())
            {
                case ProductKeyRemoval.Removed:
                    output.WriteLine(@"The registry key HKLM\SOFTWARE\Chronos was removed.");
                    break;
                case ProductKeyRemoval.KeptNotEmpty:
                    output.WriteLine(@"The registry key HKLM\SOFTWARE\Chronos was kept: something is still stored in it.");
                    break;
            }
        },
        error);

    /// <summary>Stops, then removes the service. DeleteService only marks a running service for deletion, which blocks the next registration.</summary>
    private static bool StopAndRemoveService(SetupContext setup, TextWriter output, TextWriter error)
    {
        ServiceRunState state;
        try
        {
            state = setup.Services.Query();
        }
        catch (Exception exception)
        {
            error.WriteLine($"The service manager could not be asked about Chronos: {exception.Message}");

            return false;
        }

        if (state is ServiceRunState.NotInstalled)
        {
            // Not the end of the run: the task, log source and filters may still be there.
            output.WriteLine("The Chronos service was not registered.");

            return true;
        }

        var stopped = state is ServiceRunState.Stopped || Steps.Run(
            "The Chronos service could not be stopped",
            () =>
            {
                setup.Services.Stop(StopTimeout);
                output.WriteLine("The Chronos service was stopped.");
            },
            error);

        // Attempted even if the stop failed: a service marked for deletion goes when its process exits.
        var removed = Steps.Run(
            "The Chronos service could not be removed",
            () =>
            {
                setup.Services.Remove();
                output.WriteLine("The Chronos service was removed.");
            },
            error);

        return stopped && removed;
    }

    /// <summary>Deletes the saved session on every run. Left behind, the next install would restore it from state.json. Runs after the service is gone so nothing rewrites it.</summary>
    private static bool ClearSessionState(SetupContext setup, TextWriter output, TextWriter error) => Steps.Run(
        "The saved session could not be cleared",
        () =>
        {
            setup.ClearState();
            output.WriteLine("The saved session was cleared, so installing Chronos again does not resume it.");
        },
        error);

    private static bool RemoveTask(SetupContext setup, TextWriter output, TextWriter error)
    {
        bool registered;
        try
        {
            // Exists throws when it could not ask; that must not be read as "none".
            registered = setup.Tasks.Exists();
        }
        catch (Exception exception)
        {
            error.WriteLine($"The scheduler could not be asked about the recovery task: {exception.Message}");

            return false;
        }

        if (!registered)
        {
            output.WriteLine("The recovery task was not registered.");

            return true;
        }

        return Steps.Run(
            "The recovery task could not be removed",
            () =>
            {
                setup.Tasks.Remove();
                output.WriteLine("The recovery task was removed.");
            },
            error);
    }

    /// <summary>Removes this account's autostart entry and warns about other users'.</summary>
    /// <remarks>
    /// Under the MSI this runs as LocalSystem, where HKCU is HKU\S-1-5-18, so no entry is found.
    /// That is fine: the package owns the installing user's value as a per-user component.
    /// </remarks>
    private static bool RemoveAutostart(SetupContext setup, TextWriter output, TextWriter error)
    {
        var removed = Steps.Run(
            "The autostart entry could not be removed",
            () =>
            {
                if (!setup.Autostart.IsEnabled())
                {
                    return;
                }

                setup.Autostart.Disable();
                output.WriteLine("The interface will no longer start when you log in.");
            },
            error);

        // Always printed: this runs in one account's registry and cannot reach other users' entries.
        output.WriteLine(
            "Other users on this machine keep an autostart entry that now points at nothing. "
            + "Nothing removes it by itself: it has to be deleted by hand from their own "
            + @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run.");

        return removed;
    }

    private static bool RemoveData(SetupContext setup, bool purge, TextWriter output, TextWriter error)
    {
        if (!purge)
        {
            // Uninstall during an upgrade is the common case; keep the user's lists.
            output.WriteLine("Your configuration and logs were kept. 'chronos uninstall --purge' removes them too.");

            return true;
        }

        if (!Directory.Exists(setup.Paths.DataDirectory))
        {
            output.WriteLine("There was no configuration or log left to remove.");

            return true;
        }

        // Named before deletion: this data cannot be recovered by reinstalling.
        output.WriteLine($"Removing the configuration and the logs in '{setup.Paths.DataDirectory}'.");

        return Steps.Run(
            "The configuration and logs could not be removed",
            () =>
            {
                Directory.Delete(setup.Paths.DataDirectory, recursive: true);
                output.WriteLine("The configuration and logs were removed.");
            },
            error);
    }
}

/// <summary>A writer that passes everything on and keeps a copy of it.</summary>
internal sealed class RecordingWriter(TextWriter inner) : TextWriter
{
    private readonly StringBuilder _written = new();

    public override Encoding Encoding => inner.Encoding;

    public string Written => _written.ToString();

    public override void Write(char value)
    {
        inner.Write(value);
        _written.Append(value);
    }

    public override void Write(string? value)
    {
        inner.Write(value);
        _written.Append(value);
    }
}
