using Chronos.Service.Configuration;
using Chronos.Service.Dns;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

/// <summary>Putting the interfaces back from the backup with nothing but the backup and netsh, as clean and recover need where there is no service.</summary>
public sealed class DnsRestoreTests : IDisposable
{
    private static readonly DateTimeOffset SavedAt = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly MemoryMirror _mirror = new();
    private readonly FakeDnsControl _control = new();
    private readonly FakeInterfaceDns _machine = new();
    private readonly CapturingLogger<DnsRestore> _log = new();
    private readonly DnsBackupStore _store;

    public DnsRestoreTests()
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();
        _store = new DnsBackupStore(paths, _mirror, new CapturingLogger<DnsBackupStore>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AnInterfaceThatWasOnDhcpGoesBackToDhcp()
    {
        var result = Make().Restore(Backup(Dhcp(7, "192.168.1.1")));

        Assert.Equal([(7, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.Equal(1, result.Restored);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void AStaticInterfaceGetsItsOwnListBackInTheSameOrder()
    {
        Make().Restore(Backup(Static(9, "9.9.9.9", "1.1.1.1")));

        var call = Assert.Single(_control.Calls);
        Assert.Equal(9, call.Index);
        Assert.Equal(["9.9.9.9", "1.1.1.1"], call.Servers);
    }

    [Fact]
    public void AnInterfaceThatRefusesDoesNotStopTheOthers()
    {
        _control.Refuse.Add(7);

        var result = Make().Restore(Backup(Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9")));

        Assert.Equal([7, 9], _control.Calls.Select(call => call.Index));
        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Failed);
        Assert.False(result.Complete);
    }

    [Fact]
    public void ANetshThatCannotBeStartedIsAFailureOfThatInterfaceAlone()
    {
        _control.Throw.Add(7);

        var result = Make().Restore(Backup(Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9")));

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Failed);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Adapter 7"));
    }

    [Fact]
    public void AnEntryNetshWillNotTakeIsAFailureOfThatInterfaceAlone()
    {
        // Index 0 names no adapter; the real control refuses it with ArgumentOutOfRangeException.
        var control = new ThrowingOnZero();
        var restore = new DnsRestore(_store, control, _machine, _log);

        var result = restore.Restore(Backup(Static(0, "9.9.9.9"), Static(9, "9.9.9.9")));

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void RestoreAndClearSaysSoWhenThereWasNoCopy()
    {
        var result = Make().RestoreAndClear();

        Assert.False(result.HadBackup);
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public void RestoreAndClearPutsBackWhatTheStoreHeldAndThenForgetsIt()
    {
        _store.Save(Backup(Dhcp(7, "192.168.1.1")));

        var result = Make().RestoreAndClear();

        Assert.True(result.HadBackup);
        Assert.Equal(1, result.Restored);
        Assert.Null(_store.Load());
        Assert.Null(_mirror.Held);
    }

    [Fact]
    public void RestoreAndClearKeepsTheCopyWhenAnInterfaceWasNotPutBack()
    {
        // The copy is all that says what that interface was; taking it away leaves it on 127.0.0.1 for good.
        _store.Save(Backup(Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9")));
        _control.Refuse.Add(9);

        var result = Make().RestoreAndClear();

        Assert.Equal(1, result.Failed);
        Assert.NotNull(_store.Load());
    }

    [Fact]
    public void ARestoreThatPutSomethingBackSaysHowManyAtInformation()
    {
        Make().Restore(Backup(Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9")));

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains('2'));
    }

    [Fact]
    public void AnAdapterThatMovedToANewIndexIsRestoredThere()
    {
        _machine.Inactive["{guid-7}"] = 12;

        var result = Make().Restore(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));

        Assert.Equal([(12, (IReadOnlyList<string>?)null)], _control.Calls);
        Assert.Equal(1, result.Restored);
    }

    [Fact]
    public void AnAdapterMatchesItsGuidWhateverTheCapitals()
    {
        _machine.Inactive["{GUID-7}"] = 12;

        Make().Restore(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));

        Assert.Equal(12, Assert.Single(_control.Calls).Index);
    }

    [Fact]
    public void AStoredIndexAnotherAdapterNowHoldsIsNotWrittenTo()
    {
        // These settings on somebody else's adapter would be worse than not restoring at all.
        _machine.Inactive["{someone-else}"] = 7;

        var result = Make().Restore(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));

        Assert.Empty(_control.Calls);
        Assert.Equal(["{guid-7}"], result.Absent);
        Assert.Equal(0, result.Failed);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Adapter 7"));
    }

    [Fact]
    public void AnAbsentAdapterIsTriedThroughItsStoredIndexWhenNothingElseHoldsIt()
    {
        var result = Make().Restore(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));

        Assert.Equal(7, Assert.Single(_control.Calls).Index);
        Assert.Equal(1, result.Restored);
        Assert.Empty(result.Absent);
    }

    [Fact]
    public void AnAbsentAdapterNetshCannotReachIsAbsentRatherThanFailed()
    {
        _control.Refuse.Add(7);

        var result = Make().Restore(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));

        Assert.Equal(["{guid-7}"], result.Absent);
        Assert.Equal(0, result.Failed);
        Assert.True(result.Complete);
    }

    [Fact]
    public void AnAbsentAdapterStaysInTheCopyAndDoesNotFailTheRestore()
    {
        // Gone for now, not refused. The copy is all that says what it was.
        _store.Save(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9")]));
        _machine.Inactive["{guid-9}"] = 9;
        _control.Refuse.Add(7);

        var result = Make().RestoreAndClear();

        Assert.True(result.Complete);
        var kept = _store.Load()!;
        Assert.Equal(["{guid-7}"], kept.Interfaces.Select(state => state.Guid));
        Assert.Equal(SavedAt, kept.SavedAt);
    }

    [Fact]
    public void ACopyKeptForARefusalStillCarriesTheAbsentAdapter()
    {
        _store.Save(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9")]));
        _machine.Inactive["{guid-9}"] = 9;
        _machine.Inactive["{someone-else}"] = 7;
        _control.Refuse.Add(9);

        var result = Make().RestoreAndClear();

        Assert.Equal(1, result.Failed);
        Assert.Equal(["{guid-9}"], result.Refused);
        Assert.Equal(["{guid-7}", "{guid-9}"], _store.Load()!.Interfaces.Select(state => state.Guid));
    }

    [Fact]
    public void ACopyKeptForARefusalNoLongerCarriesWhatWentBack()
    {
        // What went back is the user's again; a later restore must not reset it.
        _store.Save(Backup(Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9")));
        _control.Refuse.Add(9);

        Make().RestoreAndClear();

        Assert.Equal(["{guid-9}"], _store.Load()!.Interfaces.Select(state => state.Guid));
    }

    [Fact]
    public void AnAdapterThatComesBackIsRestoredFromTheCopyAndTheCopyGoes()
    {
        _store.Save(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));
        _control.Refuse.Add(7);
        Make().RestoreAndClear();
        _control.Refuse.Clear();
        _machine.Inactive["{guid-7}"] = 7;

        var result = Make().RestoreAndClear();

        Assert.Equal(1, result.Restored);
        Assert.Null(_store.Load());
    }

    [Fact]
    public void AnAbsentAdapterIsLoggedAsKeptAtWarning()
    {
        _control.Refuse.Add(7);

        Make().Restore(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));

        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("kept"));
    }

    [Fact]
    public void RestorePresentWritesOnlyThroughAdaptersOnTheMachineNow()
    {
        // No stored-index guess on every idle pass: netsh would run for an adapter that is gone.
        _machine.Inactive["{guid-9}"] = 9;

        var result = Make().RestorePresent(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9")]));

        Assert.Equal([9], _control.Calls.Select(call => call.Index));
        Assert.Equal(["{guid-7}"], result.Absent);
        Assert.True(result.Complete);
    }

    [Fact]
    public void RestorePresentWithNothingBackSaysNothingAboveDebug()
    {
        Make().RestorePresent(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));

        Assert.Empty(_control.Calls);
        Assert.DoesNotContain(_log.Entries, entry => entry.Level > LogLevel.Debug);
    }

    [Fact]
    public void RestorePresentCountsARefusalAsAFailure()
    {
        _machine.Inactive["{guid-7}"] = 7;
        _control.Refuse.Add(7);

        var result = Make().RestorePresent(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));

        Assert.Equal(1, result.Failed);
        Assert.Equal(["{guid-7}"], result.Refused);
    }

    [Fact]
    public void WhatIsLeftIsTheRefusedAndTheAbsentOrNothing()
    {
        var backup = new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1"), Static(8, "8.8.8.8"), Static(9, "9.9.9.9")]);
        var result = new DnsRestoreResult(HadBackup: true, 1, 1, ["{GUID-7}"]) { Refused = ["{guid-9}"] };

        Assert.Equal(["{guid-7}", "{guid-9}"], DnsRestore.Left(backup, result)!.Interfaces.Select(state => state.Guid));
        Assert.Equal(SavedAt, DnsRestore.Left(backup, result)!.SavedAt);
        Assert.Null(DnsRestore.Left(backup, new DnsRestoreResult(HadBackup: true, 3, 0, [])));
    }

    [Fact]
    public void ACopyKeptForARefusalWithNothingAbsentIsNotRewritten()
    {
        _store.Save(Backup(Dhcp(7, "192.168.1.1")));
        var writes = _mirror.Writes;
        _control.Refuse.Add(7);

        Make().RestoreAndClear();

        Assert.Equal(writes, _mirror.Writes);
    }

    [Fact]
    public void ItRefusesToBeBuiltWithoutAStoreAControlOrALogger()
    {
        Assert.Throws<ArgumentNullException>(() => new DnsRestore(null!, _control, _machine, _log));
        Assert.Throws<ArgumentNullException>(() => new DnsRestore(_store, null!, _machine, _log));
        Assert.Throws<ArgumentNullException>(() => new DnsRestore(_store, _control, null!, _log));
        Assert.Throws<ArgumentNullException>(() => new DnsRestore(_store, _control, _machine, null!));
        Assert.Throws<ArgumentNullException>(() => Make().Restore(null!));
        Assert.Throws<ArgumentNullException>(() => Make().Settle(null!, DnsRestoreResult.NoBackup));
        Assert.Throws<ArgumentNullException>(() => Make().Settle(new DnsBackup(SavedAt, [Dhcp(7)]), null!));
    }

    /// <summary>A backup of adapters that are all still on the machine, under their saved index.</summary>
    private DnsBackup Backup(params InterfaceDnsState[] states)
    {
        foreach (var state in states)
        {
            _machine.Inactive[state.Guid] = state.Index;
        }

        return new(SavedAt, states);
    }

    private static InterfaceDnsState Dhcp(int index, params string[] servers) =>
        new($"{{guid-{index}}}", index, $"Adapter {index}", IsDhcp: true, servers);

    private static InterfaceDnsState Static(int index, params string[] servers) =>
        new($"{{guid-{index}}}", index, $"Adapter {index}", IsDhcp: false, servers);

    private DnsRestore Make() => new(_store, _control, _machine, _log);

    private sealed class ThrowingOnZero : INetworkDnsControl
    {
        public bool SetStatic(int interfaceIndex, IReadOnlyList<string> servers)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(interfaceIndex, 1);
            return true;
        }

        public bool RestoreDhcp(int interfaceIndex)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(interfaceIndex, 1);
            return true;
        }
    }
}
