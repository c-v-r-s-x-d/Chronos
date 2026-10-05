using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Chronos.Service.Dns;

/// <summary>
/// The lock between the service and the command line over the interfaces' DNS settings and their
/// backup: a restore-and-clear and a takeover pass never interleave.
/// </summary>
public interface IDnsSettingsLock
{
    /// <summary>
    /// Takes the lock, waiting at most <paramref name="wait"/>; null when it stayed held. Dispose to
    /// release, on the same thread.
    /// </summary>
    IDisposable? TryAcquire(TimeSpan wait);
}

/// <summary>A named system mutex, shared by every process on the machine.</summary>
[SupportedOSPlatform("windows")]
public sealed class NamedDnsSettingsLock : IDnsSettingsLock, IDisposable
{
    /// <summary>Global, because the service and the console run in different sessions.</summary>
    public const string DefaultName = @"Global\Chronos-DnsSettings";

    private readonly string _name;
    private Mutex? _mutex;

    public NamedDnsSettingsLock()
        : this(DefaultName)
    {
    }

    public NamedDnsSettingsLock(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _name = name;
    }

    public IDisposable? TryAcquire(TimeSpan wait)
    {
        var mutex = _mutex ??= Open(_name);

        try
        {
            if (!mutex.WaitOne(wait))
            {
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // The holder died holding it; ours now, so a crashed side cannot lock the other out.
        }

        return new Held(mutex);
    }

    internal string Name => _name;

    internal MutexSecurity ReadSecurity() => (_mutex ??= Open(_name)).GetAccessControl();

    public void Dispose() => _mutex?.Dispose();

    /// <summary>Open to SYSTEM and Administrators whichever side creates it; the default DACL would block the elevated console.</summary>
    private static Mutex Open(string name)
    {
        var security = new MutexSecurity();
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(sid, null), MutexRights.FullControl, AccessControlType.Allow));
        }

        return MutexAcl.Create(initiallyOwned: false, name, out _, security);
    }

    private sealed class Held(Mutex mutex) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                mutex.ReleaseMutex();
            }
        }
    }
}
