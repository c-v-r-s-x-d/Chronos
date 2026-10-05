using Chronos.Service.Configuration;
using Chronos.Service.Dns;
using Chronos.Service.Sites;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Cli.Commands;

/// <summary>What the cleanup did. <c>RemovedSomething</c> lets install stay quiet on a clean machine.</summary>
public readonly record struct CleanResult(int ExitCode, bool RemovedSomething);

/// <summary>Takes every change Chronos makes to this machine back off it. Install and uninstall run it too.</summary>
public static class CleanCommand
{
    /// <summary>The command against the real hosts file, filter engine and interfaces.</summary>
    public static Task<int> RunAsync(HostsFile file, TextWriter output, TextWriter error)
    {
        var parts = MachineParts.Real(ChronosPaths.Default);

        return RunAsync(
            file,
            parts.OpenFilterEngine,
            () => parts.OpenDnsRestore(parts.OpenDnsBackups()),
            parts.DnsLock,
            output,
            error);
    }

    /// <summary>The command with the filter engine and DNS restore supplied.</summary>
    public static async Task<int> RunAsync(
        HostsFile file,
        Func<IWfpEngine> createEngine,
        Func<DnsRestore> openDnsRestore,
        IDnsSettingsLock dnsLock,
        TextWriter output,
        TextWriter error) =>
        (await CleanAsync(file, createEngine, openDnsRestore, dnsLock, output, error).ConfigureAwait(false)).ExitCode;

    public static async Task<CleanResult> CleanAsync(
        HostsFile file,
        Func<IWfpEngine> createEngine,
        Func<DnsRestore> openDnsRestore,
        IDnsSettingsLock dnsLock,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(createEngine);
        ArgumentNullException.ThrowIfNull(openDnsRestore);
        ArgumentNullException.ThrowIfNull(dnsLock);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // All three always run: one layer failing must not leave another applied.
        var hosts = await CleanHostsAsync(file, output, error).ConfigureAwait(false);
        var filters = CleanFilters(createEngine, output, error);
        var dns = RestoreDns(openDnsRestore, dnsLock, output, error, serviceStopped: false);

        return new CleanResult(
            hosts.ExitCode == 0 && filters.ExitCode == 0 && dns.ExitCode == 0 ? 0 : 4,
            hosts.RemovedSomething || filters.RemovedSomething || dns.RemovedSomething);
    }

    /// <summary>How long the restore waits for a service pass to release the DNS settings. Must fit in the 20 s <c>recover</c> allows its cleanup.</summary>
    public static readonly TimeSpan DnsLockWait = TimeSpan.FromSeconds(10);

    /// <summary>The tail of the absent-adapter message; uninstall leaves it out.</summary>
    internal const string AbsentStays = "their settings stay in the backup for when they return.";

    internal static DnsBackupStore OpenDnsBackups(ChronosPaths paths) =>
        new(paths, new RegistryBackupMirror(), NullLogger<DnsBackupStore>.Instance);

    /// <summary>The restore over this backup, using real netsh and adapters.</summary>
    internal static DnsRestore OpenDnsRestore(DnsBackupStore backups) => new(
        backups,
        new NetshDnsControl(NullLogger<NetshDnsControl>.Instance),
        new InterfaceDnsRegistry(),
        NullLogger<DnsRestore>.Instance);

    /// <summary>Puts the interfaces back from whichever backup copy is left. Uninstall also runs it alone after the service stops, since the service may have taken them again.</summary>
    public static CleanResult RestoreDns(
        Func<DnsRestore> openDnsRestore,
        IDnsSettingsLock dnsLock,
        TextWriter output,
        TextWriter error,
        bool serviceStopped)
    {
        ArgumentNullException.ThrowIfNull(openDnsRestore);
        ArgumentNullException.ThrowIfNull(dnsLock);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        DnsRestoreResult result;
        try
        {
            // Held across restore and clear so a service pass cannot re-take an interface in between.
            using var held = dnsLock.TryAcquire(DnsLockWait);
            if (held is null)
            {
                error.WriteLine(
                    "The DNS settings were not put back: the Chronos service kept them busy for "
                    + $"{(int)DnsLockWait.TotalSeconds} seconds. Nothing was changed; run this command again.");
                return new CleanResult(4, RemovedSomething: false);
            }

            result = openDnsRestore().RestoreAndClear();
        }
        catch (Exception exception)
        {
            error.WriteLine($"The DNS settings could not be put back: {exception.Message}");
            error.WriteLine("Run this command as Administrator: netsh and the backup in the registry need those rights.");
            return new CleanResult(4, RemovedSomething: false);
        }

        if (!result.HadBackup)
        {
            // Not an error: a machine that never ran a session looks like this.
            output.WriteLine("There was no DNS backup, so there were no DNS settings to put back.");
            return new CleanResult(0, RemovedSomething: false);
        }

        output.WriteLine($"The DNS settings of {result.Restored} interfaces were put back.");

        if (result.Restored > 0 && !serviceStopped)
        {
            // The service keeps its own copy and applies it again.
            output.WriteLine("If the Chronos service is running an active session, its next pass takes the interfaces again.");
        }

        if (result.Absent.Count > 0 && serviceStopped)
        {
            // Uninstall: once the product is gone nothing puts them back.
            error.WriteLine(
                $"{result.Absent.Count} interfaces in the DNS backup are no longer on this machine, and nothing "
                + @"will put their DNS settings back once Chronos is removed. When they return, use the README's "
                + @"'Manual fallback'; the copy stays in HKLM\SOFTWARE\Chronos, value DnsBackup.");
        }
        else if (result.Absent.Count > 0)
        {
            // Gone, not refused, so not a failure.
            output.WriteLine(
                $"{result.Absent.Count} interfaces in the DNS backup are no longer on this machine; {AbsentStays}");
        }

        // After the service stops, only interfaces that actually went back count.
        var putBack = !serviceStopped || result.Restored > 0;

        if (!result.Complete)
        {
            error.WriteLine(
                $"The DNS settings of {result.Failed} interfaces could not be put back. The backup is kept for them: "
                + "run this command again as Administrator.");
            return new CleanResult(4, putBack);
        }

        return new CleanResult(0, putBack);
    }

    /// <summary>The real filter engine, open. Shared with <c>diag</c> so both see the same filters.</summary>
    internal static IWfpEngine OpenFilterEngine()
    {
        // Persistent, not dynamic: a dynamic session sees only filters it added itself.
        var engine = new WfpEngine(persistent: true, NullLogger<WfpEngine>.Instance);
        engine.Open();

        return engine;
    }

    private static CleanResult CleanFilters(Func<IWfpEngine> createEngine, TextWriter output, TextWriter error)
    {
        IWfpEngine? engine = null;
        try
        {
            engine = createEngine();

            // No EnsureObjects: enumerating and deleting need no provider object, and this must not create one.
            var removal = engine.RemoveEverything();

            output.WriteLine(removal.Filters == 0
                ? "There were no Chronos address filters."
                : $"{removal.Filters} address filters were removed.");

            // Reported separately: a service that never opened a session leaves the provider and
            // sub-layer with no filters. The sub-layer is deleted first, so an interrupted run can
            // leave the provider alone.
            if (ObjectsRemoved(removal) is { } objects)
            {
                output.WriteLine(objects);
            }

            return new CleanResult(0, removal.Filters > 0 || removal.ProviderRemoved || removal.SubLayerRemoved);
        }
        catch (Exception exception)
        {
            // Catches everything: report what went wrong instead of a stack trace.
            error.WriteLine($"The address filters could not be removed: {exception.Message}");
            error.WriteLine(
                "Run this command as Administrator: opening the filter engine needs those rights.");
            return new CleanResult(4, RemovedSomething: false);
        }
        finally
        {
            engine?.Dispose();
        }
    }

    /// <summary>Message about the provider and sub-layer, or null when neither was removed.</summary>
    private static string? ObjectsRemoved(WfpRemoval removal) => (removal.ProviderRemoved, removal.SubLayerRemoved) switch
    {
        (true, true) => "The Chronos filter provider and its sub-layer were removed.",
        (true, false) => "The Chronos filter provider was removed.",
        (false, true) => "The Chronos filter sub-layer was removed.",
        _ => null,
    };

    private static async Task<CleanResult> CleanHostsAsync(HostsFile file, TextWriter output, TextWriter error)
    {
        var enforcer = new HostsEnforcer(file, new WindowsDnsCache(), NullLogger<HostsEnforcer>.Instance);

        try
        {
            // ClearAsync reports nothing back, so check for a block first.
            var hadBlock = enforcer.HasBlock();
            await enforcer.ClearAsync(CancellationToken.None);
            RemoveTemporaryFile(file, error);

            if (!hadBlock)
            {
                output.WriteLine("There was no Chronos block in the hosts file.");
                return new CleanResult(0, RemovedSomething: false);
            }

            output.WriteLine("The hosts block was removed.");
            output.WriteLine("If the Chronos service is running an active session, its next pass restores it.");
            return new CleanResult(0, RemovedSomething: true);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"The hosts file could not be changed: {exception.Message}");
            error.WriteLine(
                "Run this command as Administrator, or close whatever else holds the file open: "
                + "an antivirus product locking it is the other cause, and elevation does not help there.");
            return new CleanResult(4, RemovedSomething: false);
        }
    }

    private static void RemoveTemporaryFile(HostsFile file, TextWriter error)
    {
        try
        {
            // Left behind by a process killed between the write and the replace.
            File.Delete(file.TemporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The block is already gone; a leftover file is not a failure.
            error.WriteLine($"The leftover file '{file.TemporaryPath}' could not be removed: {exception.Message}");
        }
    }
}
