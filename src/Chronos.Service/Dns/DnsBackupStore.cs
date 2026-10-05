using System.Security;
using System.Text.Json;
using Chronos.Service.Configuration;
using Chronos.Service.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Dns;

/// <summary>
/// The interface settings backup, kept in a file in the data directory and in a registry value.
/// Either one alone restores the machine, so a single failing source is logged, not thrown.
/// </summary>
/// <remarks>
/// A separate file, not a field in <c>state.json</c>: the session store discards that file whole on
/// the first incompleteness, and the backup must outlive <c>Clear()</c> of a session.
/// </remarks>

public sealed class DnsBackupStore(ChronosPaths paths, IBackupMirror mirror, ILogger<DnsBackupStore> logger)
{
    /// <summary>Written over the file when it cannot be deleted. Deliberately not a document.</summary>
    private const string NotADocument =
        "This is not a DNS backup. The copy that stood here belongs to a session that has ended.";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // A condition that persists is warned about once per place.
    private readonly RepeatedDiagnostic _fileSaid = new();
    private readonly RepeatedDiagnostic _registrySaid = new();

    // HoldsACopy writes nowhere, so its conditions are neither news nor remembered.
    private static readonly Func<string, LogLevel> Quiet = _ => LogLevel.Warning;

    /// <summary>Writes both places. Throws only when neither took the copy.</summary>
    public DnsBackupSaved Save(DnsBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        // Serialized once so both places hold identical text.
        var json = JsonSerializer.Serialize(backup, Options);

        // Atomic write: half a backup cannot be restored.
        var file = Attempt(() => AtomicFile.Write(paths.DnsBackupFile, json), "file");
        var registry = Attempt(() => mirror.Write(json), "registry");

        if (file is not null && registry is not null)
        {
            // No backup at all, so the caller must not change anything.
            throw new AggregateException(
                "The DNS backup reached neither the file nor the registry.", file, registry);
        }

        if (file is null)
        {
            return registry is null ? DnsBackupSaved.BothPlaces : DnsBackupSaved.FileOnly;
        }

        // The file still holds the previous session's backup; remove it so the sources never disagree.
        Attempt(RemoveTheFileCopy, "file");

        return DnsBackupSaved.RegistryOnly;
    }

    /// <summary>
    /// Reads both sources; the fresher by <see cref="DnsBackup.SavedAt"/> wins, and a single usable
    /// copy is taken however old it is.
    /// </summary>
    public DnsBackup? Load() => Sources().Fresher;

    /// <summary>What each place holds, without choosing between them.</summary>
    public DnsBackupSources Sources()
    {
        var file = new Said(_fileSaid);
        var registry = new Said(_registrySaid);

        var sources = new DnsBackupSources(
            Parse(FromFile(logger, file.LevelOf), "file", logger, file.LevelOf),
            Parse(FromRegistry(logger, registry.LevelOf), "registry", logger, registry.LevelOf));

        file.Done();
        registry.Done();

        return sources;
    }

    /// <summary>Whether either place holds a usable copy. Does not log.</summary>
    public bool HoldsACopy() => QuietFile() is not null || QuietRegistry() is not null;

    /// <summary>Whether the copy <see cref="Load"/> would take names every one of these interfaces. Does not log.</summary>
    public bool HoldsACopyOf(IReadOnlyCollection<string> guids)
    {
        ArgumentNullException.ThrowIfNull(guids);

        // Checks the fresher copy, not either place: a prune that reached one place leaves the
        // other wider, and Load could still take the narrow one.
        return Names(new DnsBackupSources(QuietFile(), QuietRegistry()).Fresher, guids);
    }

    private DnsBackup? QuietFile() =>
        Parse(FromFile(NullLogger.Instance, Quiet), "file", NullLogger.Instance, Quiet);

    private DnsBackup? QuietRegistry() =>
        Parse(FromRegistry(NullLogger.Instance, Quiet), "registry", NullLogger.Instance, Quiet);

    private static bool Names(DnsBackup? copy, IReadOnlyCollection<string> guids) =>
        copy is not null
        && guids.All(guid => copy.Interfaces.Any(state => string.Equals(state.Guid, guid, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Clears both sources. Failures are logged, not thrown, so one cannot strand the other.</summary>
    public void Clear()
    {
        Attempt(RemoveTheFileCopy, "file");
        Attempt(mirror.Clear, "registry");
    }

    /// <summary>Deletes the file, or overwrites it when it cannot be deleted, so a stale copy is never read.</summary>
    private void RemoveTheFileCopy()
    {
        if (!File.Exists(paths.DnsBackupFile))
        {
            return;
        }

        try
        {
            File.Delete(paths.DnsBackupFile);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Usually held open by a backup agent or antivirus.
            logger.LogWarning(
                failure, "The DNS backup file could not be removed, so its contents are being written over instead.");

            File.WriteAllText(paths.DnsBackupFile, NotADocument);
        }
    }

    private string? FromFile(ILogger log, Func<string, LogLevel> levelOf)
    {
        try
        {
            return File.Exists(paths.DnsBackupFile) ? File.ReadAllText(paths.DnsBackupFile) : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            log.Log(
                levelOf("unreadable"),
                failure,
                "The DNS backup file could not be read; the registry copy is the one left.");

            return null;
        }
    }

    private string? FromRegistry(ILogger log, Func<string, LogLevel> levelOf)
    {
        try
        {
            return mirror.Read();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException)
        {
            log.Log(levelOf("unreadable"), failure, "The DNS backup in the registry could not be read.");

            return null;
        }
    }

    private static DnsBackup? Parse(string? json, string source, ILogger log, Func<string, LogLevel> levelOf)
    {
        // An empty source is normal before the first session and is not logged.
        if (json is null)
        {
            return null;
        }

        try
        {
            var backup = JsonSerializer.Deserialize<DnsBackup>(json, Options);
            if (backup is null || !backup.IsUsable())
            {
                // Valid JSON that restores nothing; the other source is asked instead.
                log.Log(levelOf("names-no-interfaces"), "The DNS backup in the {Source} names no interfaces.", source);

                return null;
            }

            return backup;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException or NotSupportedException)
        {
            log.Log(levelOf("not-a-document"), failure, "The DNS backup in the {Source} could not be read.", source);

            return null;
        }
    }

    /// <summary>Runs one half of a two-sided operation; returns the failure instead of throwing.</summary>
    private Exception? Attempt(Action action, string source)
    {
        try
        {
            action();

            return null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogError(failure, "The DNS backup could not be written to or removed from the {Source}.", source);

            return failure;
        }
    }
}

/// <summary>The usable copy each of the two places holds, read apart.</summary>
public sealed record DnsBackupSources(DnsBackup? File, DnsBackup? Registry)
{
    /// <summary>The copy a restore takes: the fresher by <see cref="DnsBackup.SavedAt"/>, the file on a tie.</summary>
    public DnsBackup? Fresher => File is null || Registry is null
        ? File ?? Registry
        : Registry.SavedAt > File.SavedAt ? Registry : File;
}

/// <summary>One place's reading in one load: Warning for news, Debug for a repeat.</summary>
file sealed class Said(RepeatedDiagnostic memory)
{
    private bool _reported;

    public LogLevel LevelOf(string condition)
    {
        _reported = true;

        return memory.IsNews(condition) ? LogLevel.Warning : LogLevel.Debug;
    }

    /// <summary>A read with nothing to say forgets the condition, so its return is news again.</summary>
    public void Done()
    {
        if (!_reported)
        {
            memory.IsNews(null);
        }
    }
}
