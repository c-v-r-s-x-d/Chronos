using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Chronos.Core.Time;
using Chronos.Ipc;
using Chronos.Service.Apps;
using Chronos.Service.Configuration;
using Chronos.Service.Diagnostics;
using Chronos.Service.Dns;
using Chronos.Service.Setup;
using Chronos.Service.Wfp;

namespace Chronos.Cli.Commands;

/// <summary>Collects a diagnostic archive describing the machine's state.</summary>
/// <remarks>
/// Any source may be missing on a broken machine; a refusal is reported as a finding in its
/// section. The archive holds the full configuration, but the event log entry and the console
/// carry no paths. Without <c>--verbose</c> the report has counts only.
/// </remarks>
public static class DiagCommand
{
    private const int DnsPort = 53;

    private const string ReportEntry = "report.txt";

    /// <summary>The logs folder inside the archive. It gets an explicit entry when empty, since a zip keeps an empty directory only if it is named.</summary>
    private const string LogsFolder = "logs/";

    private const string DiagFolder = "diag";

    public static Task<int> RunAsync(
        SetupContext setup, bool verbose, TextWriter output, TextWriter error, CancellationToken ct) =>
        RunAsync(setup, verbose, SystemClock.Instance, output, error, ct);

    /// <summary>The command with the clock supplied (file name and collection time).</summary>
    public static async Task<int> RunAsync(
        SetupContext setup,
        bool verbose,
        IClock clock,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var at = clock.UtcNow;
        var path = Destination(setup.Paths, at);

        try
        {
            await WriteArchiveAsync(setup, verbose, at, path, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The only failure that cannot go inside the package.
            error.WriteLine($"The diagnostic package could not be written: {exception.Message}");

            return Steps.Failed;
        }

        // The entry records that a package was collected, not where it went.
        setup.EventLog.Write(
            SystemEventLevel.Information,
            ChronosEvents.DiagnosticsCollected,
            "A Chronos diagnostic package was collected.");

        // The only console output, so piping it yields just the path.
        output.WriteLine(path);

        return Steps.Done;
    }

    /// <summary>Where the package goes: under the data directory, or the temp directory when there is none. The stamp is UTC so names sort by time.</summary>
    internal static string Destination(ChronosPaths paths, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var folder = Directory.Exists(paths.DataDirectory)
            ? Path.Combine(paths.DataDirectory, DiagFolder)
            : Path.GetTempPath();

        var stamp = at.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        return Path.Combine(folder, $"chronos-diag-{stamp}.zip");
    }

    /// <summary>Fills the archive. Files are copied first so the report can state what actually happened to them.</summary>
    private static async Task WriteArchiveAsync(
        SetupContext setup, bool verbose, DateTimeOffset at, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"'{path}' has no directory to write into."));

        // Create, not CreateNew: two runs in the same second name the same file; keep the later.
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        var files = new List<string>
        {
            Copy(archive, setup.Paths.ConfigFile, "config.json"),
            Copy(archive, setup.Paths.StateFile, "state.json"),
        };

        var logs = CopyLogs(archive, setup.Paths.ServiceLogDirectory);
        var report = await BuildReportAsync(setup, verbose, at, files, logs, ct).ConfigureAwait(false);

        // No byte order mark.
        using var entry = new StreamWriter(
            archive.CreateEntry(ReportEntry).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await entry.WriteAsync(report).ConfigureAwait(false);
    }

    /// <summary>Copies one file in and returns a report line. Opens directly because <see cref="File.Exists(string)"/> returns false for both absent and unreadable files.</summary>
    private static string Copy(ZipArchive archive, string path, string entryName)
    {
        try
        {
            using var source = Open(path);
            using var entry = archive.CreateEntry(entryName).Open();
            source.CopyTo(entry);

            return $"{entryName}  copied";
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Never configured, or already emptied. Say so rather than omit the entry.
            return $"{entryName}  absent";
        }
        catch (Exception exception)
        {
            return $"{entryName}  could not be copied: {Explain(exception)}";
        }
    }

    /// <summary>Copies every service log in and reports the count. <c>ServiceLogging</c> keeps seven files and rolls at ten megabytes, so the time span varies; the file names show it.</summary>
    private static IReadOnlyList<string> CopyLogs(ZipArchive archive, string directory)
    {
        string[] files;

        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (DirectoryNotFoundException)
        {
            // The folder is created anyway so the archive has the same shape on every machine.
            archive.CreateEntry(LogsFolder);

            return ["the service log directory does not exist on this machine"];
        }
        catch (Exception exception)
        {
            // Refused, which differs from absent; Directory.Exists cannot tell them apart.
            archive.CreateEntry(LogsFolder);

            return [$"the service log directory could not be listed: {Explain(exception)}"];
        }

        var notes = new List<string>();
        var copied = 0;

        foreach (var path in files)
        {
            try
            {
                using var source = Open(path);
                using var entry = archive.CreateEntry(LogsFolder + Path.GetFileName(path)).Open();
                source.CopyTo(entry);
                copied++;
            }
            catch (Exception exception)
            {
                notes.Add($"{Path.GetFileName(path)} could not be copied: {Explain(exception)}");
            }
        }

        if (copied == 0)
        {
            archive.CreateEntry(LogsFolder);
        }

        notes.Insert(0, $"{copied} log files were copied");

        return notes;
    }

    /// <summary>Opens a file with full sharing: the service usually holds today's log open for writing.</summary>
    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static async Task<string> BuildReportAsync(
        SetupContext setup,
        bool verbose,
        DateTimeOffset at,
        IReadOnlyList<string> files,
        IReadOnlyList<string> logs,
        CancellationToken ct)
    {
        var answer = await AskTheServiceAsync(setup, ct).ConfigureAwait(false);

        var report = new StringBuilder();
        report.AppendLine("Chronos diagnostic package");
        report.Append("collected  ")
            .AppendLine(at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        report.Append("detail     ").AppendLine(verbose
            ? "verbose: addresses, the hosts block and the rules themselves are listed"
            : "counts only: run 'chronos diag --verbose' for the addresses, the block and the rules");

        Section(report, "versions", Versions);
        Section(report, "files", () => files);
        Section(report, "logs", () => logs);
        Section(report, "filters", () => Filters(setup, verbose));
        Section(report, "hosts", () => Hosts(setup, verbose));
        Section(report, "port-53", () => Port53(setup));
        Section(report, "dns", () => Dns(setup));
        Section(report, "dns-l2", () => DnsLayer(setup));
        Section(report, "service", () => Service(setup));
        Section(report, "task", () => RecoveryTask(setup));
        Section(report, "layers", () => Layers(answer));
        Section(report, "rules", () => Rules(answer, verbose));
        Section(report, "app-event-source", () => AppEventSource(setup.Paths));

        return report.ToString();
    }

    /// <summary>Writes one section, or why it could not be written, so one failure does not truncate the report.</summary>
    private static void Section(StringBuilder report, string name, Func<IEnumerable<string>> read)
    {
        report.AppendLine().Append('[').Append(name).AppendLine("]");

        try
        {
            foreach (var line in read())
            {
                report.Append("  ").AppendLine(line);
            }
        }
        catch (Exception exception)
        {
            // Catches everything; the exception type tells findings apart when the message is a bare denial.
            report.Append("  ").AppendLine($"could not be read: {Explain(exception)}");
        }
    }

    /// <summary>How a failure is written into the report.</summary>
    /// <remarks>
    /// A Win32 failure is written as its number, not Windows' localized message, so reports stay
    /// searchable and comparable. The inner exception is checked too: <see cref="System.ServiceProcess"/>
    /// wraps Win32 failures in an InvalidOperationException.
    /// </remarks>
    internal static string Explain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var win32 = exception as Win32Exception ?? exception.InnerException as Win32Exception;

        if (win32 is null)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }

        var code = string.Create(
            CultureInfo.InvariantCulture, $"{win32.GetType().Name} 0x{win32.NativeErrorCode:X8} ({win32.NativeErrorCode})");

        return ReferenceEquals(win32, exception) ? code : $"{exception.GetType().Name}: {code}";
    }

    /// <summary>What the service said about itself, or why nothing did.</summary>
    private readonly record struct ServiceAnswer(StatusPayload? Status, string Silence)
    {
        public static ServiceAnswer From(StatusPayload status) => new(status, string.Empty);

        public static ServiceAnswer None(string why) => new(null, why);
    }

    private static async Task<ServiceAnswer> AskTheServiceAsync(SetupContext setup, CancellationToken ct)
    {
        try
        {
            var response = await setup.ServiceStatus(ct).ConfigureAwait(false);

            if (response?.Status is { } parsed && !AnswerCheck.IsUsable(parsed))
            {
                // Parsed but missing what the sections need; not the same as "no layers yet".
                return ServiceAnswer.None("it answered with a status this program cannot read");
            }

            return response?.Status is { } status
                ? ServiceAnswer.From(status)
                : ServiceAnswer.None(response?.Error is { } code ? ServiceText.English(code) : "it answered without a status");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ServiceAnswer.None(exception.Message);
        }
    }

    private static IEnumerable<string> Versions()
    {
        // Read from the loaded assemblies; files on disk may differ if the product was partly replaced.
        foreach (var assembly in new[]
                 {
                     typeof(Program).Assembly,
                     typeof(WfpEngine).Assembly,
                     typeof(IpcProtocol).Assembly,
                     typeof(IClock).Assembly,
                 })
        {
            yield return Row(assembly.GetName().Name ?? "unnamed assembly", VersionOf(assembly));
        }

        yield return Row("windows", RuntimeInformation.OSDescription);
        yield return Row("architecture", RuntimeInformation.OSArchitecture.ToString());
        yield return Row("runtime", RuntimeInformation.FrameworkDescription);
        yield return Row("ipc protocol", IpcProtocol.Version.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The informational version (with the commit), falling back to the assembly version.</summary>
    private static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static IEnumerable<string> Filters(SetupContext setup, bool verbose)
    {
        using var engine = setup.OpenFilterEngine();

        var filters = engine.ListOwnFilters();

        yield return $"{filters.Count} address filters of this provider are in force";

        if (!verbose)
        {
            // Filter names carry addresses, so they are listed only with --verbose.
            yield break;
        }

        foreach (var filter in filters)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"0x{filter.Id:x16}  {filter.Name}");
        }
    }

    private static IEnumerable<string> Hosts(SetupContext setup, bool verbose)
    {
        if (setup.ReadHostsBlock() is not { } block)
        {
            yield return "there is no Chronos block in the hosts file";

            yield break;
        }

        yield return $"the Chronos block is present: {block.Count} lines";

        if (!verbose)
        {
            yield break;
        }

        foreach (var line in block)
        {
            yield return line;
        }
    }

    private static IEnumerable<string> Port53(SetupContext setup)
    {
        yield return setup.OwnerOfPort(DnsPort) switch
        {
            // "Nobody holds it" and "could not tell" are different findings.
            { Known: false } => "the owner of UDP port 53 could not be determined on this machine",
            { Holder: null } => "nothing on this machine is bound to UDP port 53",
            var held => $"UDP port 53 is held by {held.Holder}",
        };
    }

    private static IEnumerable<string> Dns(SetupContext setup)
    {
        var interfaces = setup.DnsSettings();

        if (interfaces.Count == 0)
        {
            yield return "no interface on this machine is up";

            yield break;
        }

        foreach (var adapter in interfaces)
        {
            var servers = adapter.Servers.Count == 0
                ? "no resolvers configured"
                : string.Join(", ", adapter.Servers);

            yield return $"{adapter.Name} ({adapter.Kind}): {servers}";
        }
    }

    /// <summary>The resolver's port, the DNS backup in both places, and the interfaces now on 127.0.0.1. No domain names. Each part answers on its own.</summary>
    private static List<string> DnsLayer(SetupContext setup)
    {
        var lines = new List<string>();

        Part(lines, "127.0.0.1:53", () => [LoopbackPortOf(setup.ProbeLoopbackPort(DnsPort))]);
        Part(lines, "backup", () => BackupOf(setup.DnsBackups()));
        Part(lines, "on 127.0.0.1", () => [OnTheLoopback(setup.DnsSettings())]);

        return lines;
    }

    /// <summary>One part of a section: its lines, or one row saying why there are none. The first line is the named row's value.</summary>
    private static void Part(List<string> lines, string name, Func<IReadOnlyList<string>> read)
    {
        IReadOnlyList<string> values;
        try
        {
            values = read();
        }
        catch (Exception exception)
        {
            lines.Add(Row(name, $"could not be read: {Explain(exception)}"));
            return;
        }

        lines.Add(Row(name, values[0]));
        lines.AddRange(values.Skip(1));
    }

    private static string LoopbackPortOf(LoopbackPort port) => port switch
    {
        { Free: true } => "free: the resolver could take it",
        { Holder: null } => "taken, by a process this machine would not name",
        var taken => $"taken by {taken.Holder}",
    };

    private static List<string> BackupOf(DnsBackupSources sources)
    {
        var lines = new List<string>
        {
            (sources.File, sources.Registry) switch
            {
                (not null, not null) => "in the file and in the registry",
                (not null, null) => "in the file only",
                (null, not null) => "in the registry only",
                _ => "in neither place: there is nothing to restore",
            },
            Row("file", CopyOf(sources.File)),
            Row("registry", CopyOf(sources.Registry)),
        };

        if (sources.Fresher is not { } fresher)
        {
            return lines;
        }

        lines.Add(Row("restore from", ReferenceEquals(fresher, sources.File) ? "the file copy" : "the registry copy"));

        foreach (var state in fresher.Interfaces)
        {
            lines.Add(state.IsDhcp
                ? $"  {state.Name}: DHCP"
                : $"  {state.Name}: static {string.Join(", ", state.Servers)}");
        }

        return lines;
    }

    private static string CopyOf(DnsBackup? copy) => copy is null
        ? "no usable copy"
        : string.Create(CultureInfo.InvariantCulture, $"saved {Moment(copy.SavedAt)}, {copy.Interfaces.Count} interfaces");

    private static string OnTheLoopback(IReadOnlyList<InterfaceDns> interfaces)
    {
        var loopback = IPAddress.Loopback.ToString();
        var names = interfaces
            .Where(adapter => adapter.Servers.Contains(loopback, StringComparer.Ordinal))
            .Select(adapter => adapter.Name)
            .ToList();

        return names.Count == 0 ? "no active interface" : string.Join(", ", names);
    }

    private static IEnumerable<string> Service(SetupContext setup)
    {
        yield return $"the service manager reports: {setup.Services.Query()}";
    }

    private static IEnumerable<string> RecoveryTask(SetupContext setup)
    {
        yield return setup.Tasks.Exists()
            ? "the recovery task is registered, and runs at every boot"
            : "the recovery task is not registered on this machine";
    }

    private static IEnumerable<string> Layers(ServiceAnswer answer)
    {
        if (answer.Status is not { } status)
        {
            yield return $"the service did not answer: {answer.Silence}";

            yield break;
        }

        if (status.Layers.Count == 0)
        {
            yield return "the service has not reported on a layer yet";

            yield break;
        }

        foreach (var layer in status.Layers)
        {
            var reason = layer.IsAvailable
                ? "available"
                : $"unavailable: {layer.ReasonCode ?? "no reason given"}";

            yield return $"{layer.Name}  {reason}  last outcome: {layer.LastOutcome}  observed: {Moment(layer.ObservedAt)}";
        }
    }

    private static IEnumerable<string> Rules(ServiceAnswer answer, bool verbose)
    {
        if (answer.Status is not { } status)
        {
            yield return $"the service did not answer: {answer.Silence}";

            yield break;
        }

        yield return $"{status.Sites.Count} site rules and {status.Apps.Count} application rules are in force";

        if (!verbose)
        {
            yield break;
        }

        foreach (var site in status.Sites)
        {
            yield return $"site  {site.Domain}{(site.IncludeSubdomains ? "  and subdomains" : string.Empty)}";
        }

        foreach (var app in status.Apps)
        {
            yield return $"app   {app.MatchKind}  {app.Value}";
        }
    }

    /// <summary>Whether the application layer fell back to WMI events. Read from the logs because the layer status is "available" either way.</summary>
    private static IEnumerable<string> AppEventSource(ChronosPaths paths)
    {
        if (LastFallback(paths.ServiceLogDirectory) is not { } line)
        {
            yield return "the logs on this machine record no fall back to the WMI process source";

            yield break;
        }

        // Past tense with the time: the newest match may be days old and later runs may be fine.
        yield return $"the service fell back to the WMI process source at {StampOf(line)}";
        yield return "that is the most recent record of it in the retained logs, not a statement about this run";
        yield return "a run on that fallback was blind to full paths: rules written as one could not match while it lasted";
        yield return line;
    }

    /// <summary>The time at the start of a log line, in UTC like the rest of the report, or a note that it could not be read.</summary>
    internal static string StampOf(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var level = line.IndexOf(" [", StringComparison.Ordinal);
        var head = level < 0 ? line : line[..level];

        return DateTimeOffset.TryParse(
            head, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? Moment(at)
            : "a moment this report could not read off the line";
    }

    /// <summary>The last line of the retained logs that records the fallback, or null.</summary>
    /// <remarks>A missing directory gives null; any other failure propagates into <see cref="Section"/>, so "no record" and "could not look" stay distinct.</remarks>
    private static string? LastFallback(string directory)
    {
        string[] files;

        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        string? last = null;

        // Name order is date order for a daily rolling log.
        foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(Open(path));

            // Streamed: the log directory can reach seventy megabytes.
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains(ProcessEventsFactory.FellBackToWmi, StringComparison.Ordinal))
                {
                    last = line.Trim();
                }
            }
        }

        return last;
    }

    private static string Row(string name, string value) => $"{name.PadRight(16)}  {value}";

    private static string Moment(DateTimeOffset? at) => at is { } moment
        ? moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC"
        : "never";
}
