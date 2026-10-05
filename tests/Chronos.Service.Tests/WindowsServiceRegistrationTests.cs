using System.Runtime.InteropServices;
using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>What goes over to the service manager, read back on this side of the call. Nothing creates, configures or deletes a service and no rights are needed.</summary>
public sealed class WindowsServiceRegistrationTests
{
    private static ServiceDefinition Definition => ServiceDefinition.Chronos(@"C:\Program Files\Chronos\Chronos.Service.exe");

    [Fact]
    public void TheResetPeriodGoesOverInSeconds()
    {
        // dwResetPeriod is seconds while every delay beside it is milliseconds. A day sent as milliseconds is two and a half thousand years, accepted silently, so the failure count never resets.
        Assert.Equal(86400u, Sent(Definition).ResetPeriod);
    }

    [Fact]
    public void TheDelaysGoOverInMilliseconds()
    {
        // The delays are 5, 10 and 60 seconds. Sent as seconds they are read as milliseconds and the service restarts five milliseconds after it falls over.
        Assert.Equal([5000u, 10000u, 60000u], Sent(Definition).Actions.Select(action => action.Delay).ToArray());
    }

    [Fact]
    public void EveryActionRestartsTheServiceAndNotTheComputer()
    {
        Assert.All(Sent(Definition).Actions, action => Assert.Equal(ServiceInterop.SC_ACTION_RESTART, action.Type));
    }

    [Fact]
    public void TheCountTheServiceManagerReadsIsTheNumberOfActionsInTheBuffer()
    {
        // The count is what the service manager trusts; the buffer is only as long as the count
        // says. One short drops the third action, and a null pointer with a count above zero has
        // it read actions from nowhere.
        var sent = Sent(Definition);

        Assert.Equal(3u, sent.ActionCount);
        Assert.Equal((int)sent.ActionCount, sent.Actions.Count);
        Assert.True(sent.ActionsWerePointedAt);
    }

    [Fact]
    public void TheFlagThatMakesTheActionsRunIsSet()
    {
        // Info level 4, the separate call. 1 is TRUE: the failure actions then also run on a
        // failure the service itself reported, which is what a .NET host does when it dies of an
        // unhandled exception.
        Assert.Equal(1, WindowsServiceRegistration.BuildFailureActionsFlag(Definition).fFailureActionsOnNonCrashFailures);
    }

    [Fact]
    public void AServiceThatWasNeverInstalledReadsAsNotInstalled()
    {
        // A name nothing registered: asking is a read of the service database, needs no rights and leaves nothing behind.
        // The answer is an InvalidOperationException wrapping error 1060, and Query turns that one code, and only that one, into an answer.
        var registration = new WindowsServiceRegistration($"Chronos-{Guid.NewGuid():N}");

        Assert.Equal(ServiceRunState.NotInstalled, registration.Query());
    }

    /// <summary>The failure actions as the bytes the service manager would read them from.</summary>
    private static SentFailureActions Sent(ServiceDefinition definition)
    {
        var failure = WindowsServiceRegistration.AllocateFailureActions(definition);
        try
        {
            // Marshalled rather than read off the managed struct: the fields sit at offsets the
            // padding decides, and reading them back from a block of memory is the only check that
            // covers both the values and where they land.
            var block = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceInterop.SERVICE_FAILURE_ACTIONS>());
            try
            {
                Marshal.StructureToPtr(failure, block, fDeleteOld: false);

                var resetPeriod = (uint)Marshal.ReadInt32(block, 0);
                var count = (uint)Marshal.ReadInt32(block, 24);
                var buffer = Marshal.ReadIntPtr(block, 32);

                var actions = new List<(uint Type, uint Delay)>();
                if (buffer != IntPtr.Zero)
                {
                    var size = Marshal.SizeOf<ServiceInterop.SC_ACTION>();
                    for (var i = 0; i < count; i++)
                    {
                        actions.Add((
                            (uint)Marshal.ReadInt32(buffer, i * size),
                            (uint)Marshal.ReadInt32(buffer, (i * size) + 4)));
                    }
                }

                return new SentFailureActions(resetPeriod, count, actions, buffer != IntPtr.Zero);
            }
            finally
            {
                Marshal.DestroyStructure<ServiceInterop.SERVICE_FAILURE_ACTIONS>(block);
                Marshal.FreeHGlobal(block);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(failure.lpsaActions);
        }
    }

    private sealed record SentFailureActions(
        uint ResetPeriod,
        uint ActionCount,
        IReadOnlyList<(uint Type, uint Delay)> Actions,
        bool ActionsWerePointedAt);
}
