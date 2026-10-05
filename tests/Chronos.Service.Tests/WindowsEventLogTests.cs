using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security;
using Chronos.Service.Diagnostics;

namespace Chronos.Service.Tests;

public sealed class WindowsEventLogTests
{
    private sealed record Written(string Message, EventLogEntryType Type, int Id);

    private readonly List<Written> _written = [];

    private int _created;

    private int _deleted;

    /// <summary>A message file that exists on this machine, so a registered source counts as working: the test assembly itself.</summary>
    private static readonly string AFileThatIsThere = Assembly.GetExecutingAssembly().Location;

    private WindowsEventLog Log(
        bool sourceExists = true,
        Exception? throws = null,
        Exception? sourceCheckThrows = null,
        Func<string?>? messageFile = null)
    {
        var registered = sourceExists;

        return WindowsEventLog.ForTests(
            () => sourceCheckThrows is null ? registered : throw sourceCheckThrows,
            (message, type, id) =>
            {
                if (throws is not null)
                {
                    throw throws;
                }

                _written.Add(new Written(message, type, id));
            },
            () =>
            {
                _created++;
                registered = true;
            },
            () =>
            {
                _deleted++;
                registered = false;
            },
            messageFile ?? (() => AFileThatIsThere));
    }

    [Theory]
    [InlineData(SystemEventLevel.Information, EventLogEntryType.Information)]
    [InlineData(SystemEventLevel.Warning, EventLogEntryType.Warning)]
    [InlineData(SystemEventLevel.Error, EventLogEntryType.Error)]
    public void Write_UsesTheEntryTypeTheLevelMeans(SystemEventLevel level, EventLogEntryType expected)
    {
        Assert.True(Log().Write(level, ChronosEvents.RecoveryCleared, "The machine was cleared."));

        Assert.Equal(expected, Assert.Single(_written).Type);
    }

    [Fact]
    public void Write_MapsALevelThisBuildDoesNotKnowToError()
    {
        // The arm cannot be deleted - CS8524 - so it has to be loud. A level added later and not
        // mapped here is louder than the three that exist, never quieter.
        Assert.True(Log().Write((SystemEventLevel)999, ChronosEvents.RecoveryFailed, "An unmapped level."));

        Assert.Equal(EventLogEntryType.Error, Assert.Single(_written).Type);
    }

    [Fact]
    public void Write_KeepsTheEventIdItWasGiven()
    {
        Log().Write(SystemEventLevel.Error, ChronosEvents.RecoveryCleared, "The machine was cleared.");

        Assert.Equal(ChronosEvents.RecoveryCleared, Assert.Single(_written).Id);
    }

    [Fact]
    public void Write_PutsTheMessageItWasGivenInTheEntry()
    {
        // The reason is the part an administrator reads, so it must arrive; discarding it would leave every other test here green.
        Log().Write(SystemEventLevel.Error, ChronosEvents.RecoveryFailed, "The engine refused the filter.");

        Assert.Equal("The engine refused the filter.", Assert.Single(_written).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Write_ReplacesAMissingMessageInsteadOfThrowing(string? message)
    {
        // The recovery procedure builds its message from a caught exception, and Message is empty
        // for plenty of them - an unmapped Win32Exception among them. Throwing here would kill
        // the procedure inside its own logging call and leave the machine blocked.
        Assert.True(Log().Write(SystemEventLevel.Error, ChronosEvents.RecoveryFailed, message!));

        Assert.Equal(WindowsEventLog.NoMessageSupplied, Assert.Single(_written).Message);
    }

    [Fact]
    public void Write_AnswersFalseWithoutARegisteredSource()
    {
        // The recovery procedure runs on a machine whose installation is broken; an unregistered
        // source is one of the shapes "broken" takes. It must still take the block off.
        Assert.False(Log(sourceExists: false).Write(SystemEventLevel.Error, ChronosEvents.RecoveryCleared, "x"));
        Assert.Empty(_written);
    }

    [Fact]
    public void Write_SwallowsAFailingLogAndSaysSo()
    {
        var log = Log(throws: new InvalidOperationException("The event log is full."));

        Assert.False(log.Write(SystemEventLevel.Error, ChronosEvents.RecoveryCleared, "x"));
    }

    [Fact]
    public void SourceExists_AnswersFalseForAnyFailureOfTheCheck()
    {
        // EventLog.SourceExists reads HKLM\SYSTEM\CurrentControlSet\Services\EventLog, and a hive with damaged ACLs is the broken machine this class exists for.
        // Any exception type must be caught, including UnauthorizedAccessException and Win32Exception.
        foreach (var failure in TheWaysTheRegistryFails())
        {
            Assert.False(Log(sourceCheckThrows: failure).SourceExists());
        }
    }

    [Fact]
    public void Write_AnswersFalseWhenTheSourceCheckItselfFails()
    {
        // The check sits inside the try; its escapees would otherwise leave Write by throwing, like a Write that throws.
        foreach (var failure in TheWaysTheRegistryFails())
        {
            Assert.False(Log(sourceCheckThrows: failure).Write(SystemEventLevel.Error, ChronosEvents.RecoveryFailed, "x"));
        }

        Assert.Empty(_written);
    }

    private static IEnumerable<Exception> TheWaysTheRegistryFails() =>
    [
        new InvalidOperationException("The log could not be opened."),
        new SecurityException("The key is not readable."),
        new UnauthorizedAccessException("Access to the registry key is denied."),
        new Win32Exception(5),
        new IOException("The hive is damaged."),
    ];

    [Fact]
    public void EventIdsAreDistinct()
    {
        // They are what an administrator filters the log by, and two events sharing an id goes unnoticed until someone reads the log.
        // Read by reflection so an added id is covered the day it is written.
        var ids = typeof(ChronosEvents)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsLiteral && field.FieldType == typeof(int))
            .ToDictionary(field => field.Name, field => (int)field.GetRawConstantValue()!);

        // The query itself has to be right: an empty set would pass the check below.
        Assert.Contains(nameof(ChronosEvents.Installed), ids.Keys);

        var collisions = ids
            .GroupBy(id => id.Value)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(id => id.Key))}")
            .ToList();

        Assert.Empty(collisions);
    }

    [Fact]
    public void EnsureSource_RegistersTheSourceWhenThereIsNone()
    {
        Log(sourceExists: false).EnsureSource();

        Assert.Equal(1, _created);
    }

    [Fact]
    public void EnsureSource_LeavesAnAlreadyRegisteredSourceAlone()
    {
        // Re-registering is not harmless: CreateEventSource throws on a source that exists, and
        // the installer runs this over a repair of a working installation.
        Log().EnsureSource();

        Assert.Equal(0, _created);
        Assert.Equal(0, _deleted);
    }

    [Fact]
    public void EnsureSource_RegistersAgainWhenTheRecordedMessageResourcesAreGone()
    {
        // CreateEventSource records an absolute path to System.Diagnostics.EventLog.Messages.dll beside the creating process, so a machine installed from a build directory since deleted keeps a pointer into nothing:
        // Event Viewer shows "The description for Event ID (2001) cannot be found" instead of why the service failed.
        Log(messageFile: () => @"C:\a-directory-that-was-deleted\System.Diagnostics.EventLog.Messages.dll")
            .EnsureSource();

        // Deleted first: CreateEventSource refuses a source that is already registered, so a
        // repair that only created would be no repair at all.
        Assert.Equal(1, _deleted);
        Assert.Equal(1, _created);
    }

    [Fact]
    public void EnsureSource_RegistersAgainWhenTheSourceRecordsNoMessageResourcesAtAll()
    {
        Log(messageFile: () => null).EnsureSource();

        Assert.Equal(1, _deleted);
        Assert.Equal(1, _created);
    }

    [Fact]
    public void EnsureSource_LeavesTheRegistrationAloneWhenItCannotReadWhereItPoints()
    {
        // The hive is HKLM on a machine being repaired. Not being able to look is no evidence of a bad pointer, and deleting a working registration on that guess is worse than leaving a stale one.
        Log(messageFile: () => throw new UnauthorizedAccessException("The key could not be opened."))
            .EnsureSource();

        Assert.Equal(0, _deleted);
        Assert.Equal(0, _created);
    }

    [Fact]
    public void RemoveSource_UnregistersTheSourceItFinds()
    {
        Log().RemoveSource();

        Assert.Equal(1, _deleted);
    }

    [Fact]
    public void RemoveSource_DoesNothingWhenThereIsNoSource()
    {
        // An uninstall has to leave a half-installed machine looking like a machine
        // that never had the product, and must not fail over the half that was never there.
        Log(sourceExists: false).RemoveSource();

        Assert.Equal(0, _deleted);
    }

    [Fact]
    public void ForTests_RefusesEveryProductionSeam()
    {
        // A seam that can be handed the real platform is not a seam (as in IpcServer.ForTests). These five method groups are named and never called; naming them is the test.
        static bool NoCheck() => false;
        static void NoWrite(string message, EventLogEntryType type, int id) { }
        static void NoChange() { }
        static string? NoPath() => null;

        Assert.Throws<ArgumentException>(() => WindowsEventLog.ForTests(
            WindowsEventLog.SourceIsRegistered, NoWrite, NoChange, NoChange, NoPath));
        Assert.Throws<ArgumentException>(() => WindowsEventLog.ForTests(
            NoCheck, WindowsEventLog.WriteToApplicationLog, NoChange, NoChange, NoPath));
        Assert.Throws<ArgumentException>(() => WindowsEventLog.ForTests(
            NoCheck, NoWrite, WindowsEventLog.CreateSourceInRegistry, NoChange, NoPath));
        Assert.Throws<ArgumentException>(() => WindowsEventLog.ForTests(
            NoCheck, NoWrite, NoChange, WindowsEventLog.DeleteSourceFromRegistry, NoPath));
        Assert.Throws<ArgumentException>(() => WindowsEventLog.ForTests(
            NoCheck, NoWrite, NoChange, NoChange, WindowsEventLog.MessageFileOfRegisteredSource));
    }
}
