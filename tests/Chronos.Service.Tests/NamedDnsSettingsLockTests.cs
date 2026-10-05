using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Chronos.Service.Dns;

namespace Chronos.Service.Tests;

/// <summary>The real named mutex, under a per-test name. The other side is another thread: a mutex is owned per thread.</summary>
[SupportedOSPlatform("windows")]
public sealed class NamedDnsSettingsLockTests : IDisposable
{
    private readonly NamedDnsSettingsLock _lock = new(@"Local\chronos-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => _lock.Dispose();

    [Fact]
    public void TheProductsLockIsGlobalBecauseTheServiceAndTheConsoleRunInDifferentSessions()
    {
        Assert.StartsWith(@"Global\", NamedDnsSettingsLock.DefaultName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLockBuiltWithoutANameIsTheProducts()
    {
        // Built, not opened: no Global\ object is created by the suite.
        using var product = new NamedDnsSettingsLock();

        Assert.Equal(@"Global\Chronos-DnsSettings", product.Name);
    }

    [Fact]
    public void SystemAndAdministratorsBothHoldFullControl()
    {
        // Whichever side creates it, the other must be able to take and release it.
        var rules = _lock.ReadSecurity()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<MutexAccessRule>()
            .ToList();

        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            var who = new SecurityIdentifier(sid, null);
            Assert.Contains(
                rules,
                rule => rule.IdentityReference.Equals(who)
                    && rule.AccessControlType == AccessControlType.Allow
                    && rule.MutexRights == MutexRights.FullControl);
        }
    }

    [Fact]
    public void TheWaitIsHonouredUntilTheOtherSideLetsGo()
    {
        using var taken = new ManualResetEventSlim();
        var other = new Thread(() =>
        {
            using var held = _lock.TryAcquire(TimeSpan.Zero);
            taken.Set();
            Thread.Sleep(100);
        });
        other.Start();
        taken.Wait();

        try
        {
            Assert.True(Take(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            other.Join();
        }
    }

    [Fact]
    public void AFreeLockIsTakenAndLetGo()
    {
        var held = _lock.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(held);
        held.Dispose();

        Assert.True(OnAnotherThread(() => Take(TimeSpan.Zero)));
    }

    [Fact]
    public void ALockTheOtherSideHoldsIsNotTakenOnceTheWaitRunsOut()
    {
        using var release = new ManualResetEventSlim();
        using var taken = new ManualResetEventSlim();
        var other = new Thread(() =>
        {
            using var held = _lock.TryAcquire(TimeSpan.Zero);
            taken.Set();
            release.Wait();
        });
        other.Start();
        taken.Wait();

        try
        {
            Assert.Null(_lock.TryAcquire(TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            release.Set();
            other.Join();
        }

        Assert.True(Take(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ALockWhoseHolderDiedHoldingItIsTaken()
    {
        // A crashed service or console must not lock the other side out for good.
        var other = new Thread(() => _lock.TryAcquire(TimeSpan.Zero));
        other.Start();
        other.Join();

        Assert.True(Take(TimeSpan.FromSeconds(5)));
    }

    private bool Take(TimeSpan wait)
    {
        using var held = _lock.TryAcquire(wait);

        return held is not null;
    }

    private static bool OnAnotherThread(Func<bool> work)
    {
        var answer = false;
        var thread = new Thread(() => answer = work());
        thread.Start();
        thread.Join();

        return answer;
    }
}
