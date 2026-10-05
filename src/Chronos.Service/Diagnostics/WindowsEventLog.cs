using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Chronos.Service.Diagnostics;

[SupportedOSPlatform("windows")]
public sealed class WindowsEventLog : ISystemEventLog
{
    // The Application log: a private log would hide the entries where nobody looks.
    public const string LogName = "Application";

    public const string SourceName = "Chronos";

    public const string NoMessageSupplied = "(the caller supplied no message)";

    private readonly Func<bool> _sourceExists;

    private readonly Action<string, EventLogEntryType, int> _write;

    private readonly Action _createSource;

    private readonly Action _deleteSource;

    private readonly Func<string?> _recordedMessageFile;

    public WindowsEventLog()
        : this(
            SourceIsRegistered,
            WriteToApplicationLog,
            CreateSourceInRegistry,
            DeleteSourceFromRegistry,
            MessageFileOfRegisteredSource)
    {
    }

    private WindowsEventLog(
        Func<bool> sourceExists,
        Action<string, EventLogEntryType, int> write,
        Action createSource,
        Action deleteSource,
        Func<string?> recordedMessageFile)
    {
        _sourceExists = sourceExists;
        _write = write;
        _createSource = createSource;
        _deleteSource = deleteSource;
        _recordedMessageFile = recordedMessageFile;
    }

    /// <summary>
    /// The same type with the platform replaced. Every call that reaches Windows goes through these
    /// delegates, and a delegate from this assembly is refused so a test cannot be handed the real
    /// ones. A lambda that calls <see cref="EventLog"/> itself is not caught.
    /// </summary>
    internal static WindowsEventLog ForTests(
        Func<bool> sourceExists,
        Action<string, EventLogEntryType, int> write,
        Action createSource,
        Action deleteSource,
        Func<string?> recordedMessageFile) =>
        new(
            NotThePlatform(sourceExists, nameof(sourceExists)),
            NotThePlatform(write, nameof(write)),
            NotThePlatform(createSource, nameof(createSource)),
            NotThePlatform(deleteSource, nameof(deleteSource)),
            NotThePlatform(recordedMessageFile, nameof(recordedMessageFile)));

    private static T NotThePlatform<T>(T seam, string parameter)
        where T : Delegate
    {
        ArgumentNullException.ThrowIfNull(seam, parameter);

        if (seam.Method.DeclaringType?.Assembly == typeof(WindowsEventLog).Assembly)
        {
            throw new ArgumentException(
                "A test seam may not be one of this type's own: those reach the machine's Application "
                + "log and the event source registry under HKLM, which is the whole of what this "
                + "overload exists to keep a test away from.",
                parameter);
        }

        return seam;
    }

    public bool SourceExists()
    {
        try
        {
            return _sourceExists();
        }
        catch (Exception)
        {
            // Catch everything: EventLog.SourceExists reads HKLM, and UnauthorizedAccessException
            // and Win32Exception (with a localised Message) can both come out of a damaged hive.
            // Not knowing is the same answer as not having a source.
            return false;
        }
    }

    public bool Write(SystemEventLevel level, int eventId, string message)
    {
        // Never throws: callers build the message from caught exceptions, whose Message is often empty.
        var written = string.IsNullOrWhiteSpace(message) ? NoMessageSupplied : message;

        try
        {
            if (!SourceExists())
            {
                // Registration is an install-time action; without it the write is denied.
                return false;
            }

            _write(written, EntryType(level), eventId);
            return true;
        }
        catch (Exception)
        {
            // A failure of the log itself has nowhere left to go.
            return false;
        }
    }

    /// <summary>
    /// Registers the source, and re-registers one whose message resources are gone.
    /// <c>CreateEventSource</c> records an absolute path to the messages DLL, and a deleted or moved
    /// copy leaves Event Viewer unable to show the entry text. An unreadable path is left alone.
    /// </summary>
    public void EnsureSource()
    {
        if (!SourceExists())
        {
            _createSource();

            return;
        }

        if (!RecordedMessageFileIsMissing())
        {
            return;
        }

        // CreateEventSource refuses a source that is already registered.
        _deleteSource();
        _createSource();
    }

    /// <summary>Whether the registered message resources are missing. False when it cannot tell.</summary>
    private bool RecordedMessageFileIsMissing()
    {
        string? recorded;
        try
        {
            recorded = _recordedMessageFile();
        }
        catch (Exception)
        {
            // Could not look: leave the registration alone.
            return false;
        }

        if (recorded is null)
        {
            // No message resources recorded at all.
            return true;
        }

        // The value may be a semicolon-separated list; one missing file is enough to lose the text.
        var paths = recorded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return paths.Length == 0 || paths.Any(path => !File.Exists(path));
    }

    public void RemoveSource()
    {
        if (SourceExists())
        {
            _deleteSource();
        }
    }

    // Internal so tests can name them to prove ForTests refuses them; tests never call them.
    internal static bool SourceIsRegistered() => EventLog.SourceExists(SourceName);

    internal static void WriteToApplicationLog(string message, EventLogEntryType type, int eventId)
    {
        using var log = new EventLog(LogName) { Source = SourceName };
        log.WriteEntry(message, type, eventId);
    }

    internal static void CreateSourceInRegistry() =>
        EventLog.CreateEventSource(new EventSourceCreationData(SourceName, LogName));

    internal static void DeleteSourceFromRegistry() => EventLog.DeleteEventSource(SourceName);

    /// <summary>
    /// The message resource path the registered source records, or null. Read from the registry
    /// because <see cref="EventLog"/> cannot report it.
    /// </summary>
    internal static string? MessageFileOfRegisteredSource()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\EventLog\{LogName}\{SourceName}", writable: false);

        // The value is REG_EXPAND_SZ; GetValue expands %SystemRoot%.
        return key?.GetValue("EventMessageFile") as string;
    }

    private static EventLogEntryType EntryType(SystemEventLevel level) => level switch
    {
        SystemEventLevel.Information => EventLogEntryType.Information,
        SystemEventLevel.Warning => EventLogEntryType.Warning,
        SystemEventLevel.Error => EventLogEntryType.Error,

        // An unmapped level is a defect here; show it loudly, not as Information.
        _ => EventLogEntryType.Error,
    };
}
