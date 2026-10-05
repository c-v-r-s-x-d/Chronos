using Chronos.Core.Enforcement;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Sites;

public sealed class HostsEnforcer(HostsFile file, IDnsCache dns, ILogger<HostsEnforcer> logger) : IEnforcer
{
    public const string LayerName = "hosts";

    // Stable codes, not sentences: reasons reach a bilingual UI. Detail goes to the log.
    public const string NotWritable = "hosts.not-writable";
    public const string DirectoryMissing = "hosts.directory-missing";

    public string Name => LayerName;

    public Task<EnforcerAvailability> ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(file.FilePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return Task.FromResult(EnforcerAvailability.Unavailable(DirectoryMissing));
        }

        if (!File.Exists(file.FilePath))
        {
            // The write creates a missing hosts file.
            return Task.FromResult(EnforcerAvailability.Available);
        }

        try
        {
            // FileMode.Open never truncates, so opening for write here changes nothing.
            using var probe = new FileStream(file.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return Task.FromResult(EnforcerAvailability.Available);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // E.g. antivirus holding the file: report unavailable, not a failed cycle; other layers still apply.
            logger.LogWarning(exception, "The hosts file cannot be opened for writing.");
            return Task.FromResult(EnforcerAvailability.Unavailable(NotWritable));
        }
    }

    public Task<ReconcileResult> ReconcileAsync(EnforcementPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();

        try
        {
            return Task.FromResult(Reconcile(plan));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stable code, as in ProbeAsync, not an exception message.
            logger.LogWarning(exception, "The hosts file could not be written.");
            return Task.FromResult(ReconcileResult.Failed(Name, NotWritable, exception));
        }
    }

    /// <summary>Whether the file carries a block. <see cref="ClearAsync"/> reports nothing, so the CLI uses this.</summary>
    public bool HasBlock() => file.ReadBlock() is not null;

    public Task ClearAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // No availability check: an unavailable layer must still be able to take back its block.
        var content = file.Read();
        var cleared = HostsBlock.Remove(content);
        if (string.Equals(cleared, content, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        file.Write(cleared);
        Flush();
        logger.LogInformation("The hosts block was removed.");

        return Task.CompletedTask;
    }

    private ReconcileResult Reconcile(EnforcementPlan plan)
    {
        var content = file.Read();
        var current = HostsBlock.Read(content);
        var desired = plan.Sites.Count == 0 ? null : HostEntries.Render(plan.Sites);

        if (IsSame(current, desired))
        {
            return ReconcileResult.Unchanged(Name);
        }

        var currentNames = HostEntries.NamesIn(current ?? []);
        var desiredNames = HostEntries.NamesIn(desired ?? []);

        file.Write(desired is null ? HostsBlock.Remove(content) : HostsBlock.Write(content, desired));
        Flush();

        logger.LogDebug("The hosts block now holds {Count} names.", desiredNames.Count);

        var skipped = HostEntries.SkippedCount(plan.Sites);
        if (skipped > 0)
        {
            // A count at Debug only: domain names above Debug would turn the log into a browsing history.
            logger.LogDebug("Skipped {Count} host names that are not ASCII.", skipped);
        }

        return ReconcileResult.Changed(
            Name,
            applied: desiredNames.Except(currentNames, StringComparer.Ordinal).Count(),
            removed: currentNames.Except(desiredNames, StringComparer.Ordinal).Count());
    }

    private static bool IsSame(IReadOnlyList<string>? current, IReadOnlyList<string>? desired) =>
        (current, desired) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => current.SequenceEqual(desired, StringComparer.Ordinal),
        };

    private void Flush()
    {
        if (!dns.Flush())
        {
            logger.LogDebug("The resolver cache could not be flushed; blocked names expire with their TTL.");
        }
    }
}
