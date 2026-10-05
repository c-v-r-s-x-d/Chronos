using Microsoft.Extensions.Logging;

namespace Chronos.Service.Dns;

/// <summary>
/// What a restore did: whether there was a backup, how many interfaces went back or refused, and
/// which adapters (by GUID) are no longer on the machine.
/// </summary>
public sealed record DnsRestoreResult(bool HadBackup, int Restored, int Failed, IReadOnlyList<string> Absent)
{
    public static DnsRestoreResult NoBackup { get; } = new(HadBackup: false, 0, 0, []);

    /// <summary>Every adapter still on the machine went back; absent ones do not count against it.</summary>
    public bool Complete => Failed == 0;

    public IReadOnlyList<string> Refused { get; init; } = [];
}

/// <summary>
/// Puts every interface in the backup back the way it was: DHCP to DHCP, a static list in the same
/// order. Shared by the service and the CLI's <c>clean</c> and <c>recover</c>.
/// </summary>
/// <remarks>
/// Adapters are written through their current index, found by GUID: a reinstalled adapter's
/// stored index may now name another adapter.
/// </remarks>
public sealed class DnsRestore
{
    private readonly DnsBackupStore _backups;
    private readonly INetworkDnsControl _control;
    private readonly IInterfaceDns _interfaces;
    private readonly ILogger _logger;

    public DnsRestore(DnsBackupStore backups, INetworkDnsControl control, IInterfaceDns interfaces, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(logger);

        _backups = backups;
        _control = control;
        _interfaces = interfaces;
        _logger = logger;
    }

    /// <summary>Restores from whichever copy the store holds, then settles the copy.</summary>
    public DnsRestoreResult RestoreAndClear()
    {
        if (_backups.Load() is not { } backup)
        {
            return DnsRestoreResult.NoBackup;
        }

        var result = Restore(backup);
        Settle(backup, result);

        return result;
    }

    /// <summary>Restores the settings of this backup. The store is not touched.</summary>
    public DnsRestoreResult Restore(DnsBackup backup) => Restore(backup, reachAbsent: true);

    /// <summary>The idle-pass retry: only adapters on the machine now. Absent ones are skipped quietly.</summary>
    public DnsRestoreResult RestorePresent(DnsBackup backup) => Restore(backup, reachAbsent: false);

    /// <summary>The refused and absent interfaces, in backup order, with the backup's timestamp. Null when none.</summary>
    public static DnsBackup? Left(DnsBackup backup, DnsRestoreResult result)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(result);

        var left = result.Absent.Concat(result.Refused).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = backup.Interfaces.Where(state => left.Contains(state.Guid)).ToList();

        return kept.Count == 0 ? null : backup with { Interfaces = kept };
    }

    /// <summary>Leaves in the store exactly what is still to restore: cleared when nothing is, rewritten when some went back.</summary>
    public void Settle(DnsBackup backup, DnsRestoreResult result)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(result);

        // Restored interfaces are dropped from the backup, or a later restore would reset them.
        var left = Left(backup, result);

        if (left is null)
        {
            _backups.Clear();
        }
        else if (left.Interfaces.Count < backup.Interfaces.Count)
        {
            _backups.Save(left);
        }
    }

    private DnsRestoreResult Restore(DnsBackup backup, bool reachAbsent)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var indexes = _interfaces.Indexes();
        var restored = 0;
        var refused = new List<string>();
        var absent = new List<string>();

        foreach (var state in backup.Interfaces)
        {
            if (indexes.TryGetValue(state.Guid, out var current))
            {
                if (TryRestore(state, current))
                {
                    restored++;
                }
                else
                {
                    refused.Add(state.Guid);
                }

                continue;
            }

            // Not on the machine. The stored index is tried only while no other adapter holds it.
            if (reachAbsent && !indexes.Values.Contains(state.Index) && TryRestore(state, state.Index))
            {
                restored++;
                continue;
            }

            if (reachAbsent)
            {
                _logger.LogWarning(
                    "{Interface} is no longer on this machine; its DNS settings are kept in the backup until it returns.",
                    state.Name);
            }
            else
            {
                _logger.LogDebug("{Interface} is still not on this machine.", state.Name);
            }

            absent.Add(state.Guid);
        }

        if (restored > 0)
        {
            _logger.LogInformation("Put back the DNS settings of {Count} interfaces.", restored);
        }

        return new DnsRestoreResult(HadBackup: true, restored, refused.Count, absent) { Refused = refused };
    }

    private bool TryRestore(InterfaceDnsState state, int index)
    {
        try
        {
            var done = state.IsDhcp
                ? _control.RestoreDhcp(index)
                : _control.SetStatic(index, state.Servers);

            if (!done)
            {
                _logger.LogWarning(
                    "The DNS settings of {Interface} (index {Index}) could not be put back.", state.Name, index);
            }

            return done;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One interface failing must not leave the rest on 127.0.0.1.
            _logger.LogWarning(
                exception, "The DNS settings of {Interface} (index {Index}) could not be put back.", state.Name, index);

            return false;
        }
    }
}
