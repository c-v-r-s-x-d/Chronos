using Chronos.Service.Configuration;
using Chronos.Service.Dns;
using Chronos.Service.Sites;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class CleanCommandTests : IDisposable
{
    private static readonly DateTimeOffset SavedAt = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeWfpEngine _engine = new();
    private readonly MemoryMirror _mirror = new();
    private readonly FakeInterfaceDns _machine = new();
    private readonly FakeDnsControl _control;
    private readonly DnsBackupStore _backups;
    private readonly FakeDnsSettingsLock _lock = new();
    private readonly string _backupFile;

    public CleanCommandTests()
    {
        Directory.CreateDirectory(_root);

        var paths = new ChronosPaths(Path.Combine(_root, "data"), Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();
        _backups = new DnsBackupStore(paths, _mirror, NullLogger<DnsBackupStore>.Instance);
        _control = new FakeDnsControl(_machine);
        _backupFile = paths.DnsBackupFile;
    }

    private Exception? DnsThrows { get; set; }

    private string HostsPath => Path.Combine(_root, "hosts");

    private Exception? EngineThrows { get; set; }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Clean_RemovesTheBlockWithoutTheServiceRunning()
    {
        const string foreign = "127.0.0.1 localhost\r\n";
        File.WriteAllText(HostsPath, HostsBlock.Write(foreign, ["0.0.0.0 example.com"]));
        var output = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Equal(foreign, File.ReadAllText(HostsPath));
        Assert.Contains("was removed", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_SaysSoWhenThereWasNothingToRemove()
    {
        // Claiming to have removed a block that was never there would be untrue.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        var output = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("was removed", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("no Chronos block", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_RemovesATemporaryFileLeftBehindByAKilledWrite()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        var file = new HostsFile(HostsPath);
        File.WriteAllText(file.TemporaryPath, "half of a write\r\n");

        await CleanAsync(file, TextWriter.Null, TextWriter.Null);

        Assert.Equal([HostsPath], Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Clean_ReportsAnAccessFailureOnTheErrorWriter()
    {
        File.WriteAllText(HostsPath, HostsBlock.Write(string.Empty, ["0.0.0.0 example.com"]));
        using var exclusive = new FileStream(HostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, error);

        // 3 is the code for a service that cannot be reached; this failure is a different one and elevation is only one of its two causes.
        Assert.Equal(4, exitCode);
        Assert.DoesNotContain("was removed", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Administrator", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("antivirus", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_RemovesTheAddressFiltersToo()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        _engine.Seed("Chronos|left|behind");
        _engine.Seed("Chronos|by a killed|service");
        var output = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        // The filters are persistent, so only this command removes what a service that no longer starts left behind.
        Assert.Equal(0, exitCode);
        Assert.Equal(1, _engine.RemoveEverythingCalls);
        Assert.Contains("2 address filters", output.ToString(), StringComparison.Ordinal);
        Assert.True(_engine.Disposed);
    }

    [Fact]
    public async Task Clean_SaysSoWhenThereWereNoAddressFilters()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        var output = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Contains("no Chronos address filters", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_SaysTheProviderWentEvenWhenThereWereNoFilters()
    {
        // A service that ran without a session leaves no filters but a provider and a sub-layer. Reporting
        // only "no Chronos address filters" would call the machine clean while two platform objects were removed.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        _engine.SeedProviderWithoutFilters();
        var output = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Contains("provider", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_NamesTheProviderAloneWhenTheSubLayerHadAlreadyGone()
    {
        // The half-cleaned machine, which is why the two objects are reported apart: RemoveEverything
        // deletes the sub-layer first, so a run killed between the deletes leaves the provider alone.
        // `recover` and `uninstall` run this same path against that state.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        _engine.SeedObjects(provider: true, subLayer: false);
        var output = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Contains("provider", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sub-layer", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_NamesTheSubLayerAloneWhenTheProviderHadAlreadyGone()
    {
        // The other half: naming the provider here would claim something was removed that was not there.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        _engine.SeedObjects(provider: false, subLayer: true);
        var output = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Contains("sub-layer", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_SaysNothingWasThereWhenNothingWasThere()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        var output = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Contains("no Chronos address filters", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_ReportsAFilterFailureAsAnAccessFailure()
    {
        File.WriteAllText(HostsPath, HostsBlock.Write(string.Empty, ["0.0.0.0 example.com"]));
        EngineThrows = new InvalidOperationException("FwpmEngineOpen0 failed with 0x80070005.");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, error);

        // The same 4 the hosts file uses: it could not be taken off, and elevation is the first thing to try.
        Assert.Equal(4, exitCode);
        Assert.Contains("Administrator", error.ToString(), StringComparison.Ordinal);

        // The hosts block still went: a layer that cannot be cleared must not keep the others applied.
        Assert.Contains("was removed", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, File.ReadAllText(HostsPath));
    }

    [Fact]
    public async Task Clean_RemovesTheFiltersEvenWhenTheHostsFileCannotBeChanged()
    {
        File.WriteAllText(HostsPath, HostsBlock.Write(string.Empty, ["0.0.0.0 example.com"]));
        using var exclusive = new FileStream(HostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _engine.Seed("Chronos|left|behind");

        var exitCode = await CleanAsync(new HostsFile(HostsPath), TextWriter.Null, TextWriter.Null);

        Assert.Equal(4, exitCode);
        Assert.Equal(1, _engine.RemoveEverythingCalls);
    }

    [Fact]
    public async Task Clean_PutsTheResolversBackWithoutTheService()
    {
        // A service that died with the interfaces on 127.0.0.1 leaves this command as the one way back.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9", "1.1.1.1"));
        var output = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.True(_machine.Of(7).IsDhcp);
        Assert.Equal(["9.9.9.9", "1.1.1.1"], _machine.Of(9).Servers);
        Assert.Contains("DNS settings of 2 interfaces were put back", output.ToString(), StringComparison.Ordinal);
        Assert.Null(_backups.Load());
    }

    [Fact]
    public async Task Clean_PutsTheResolversBackFromTheRegistryWhenTheFileIsGone()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));
        File.Delete(_backupFile);

        var exitCode = await CleanAsync(new HostsFile(HostsPath), TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Equal(["9.9.9.9"], _machine.Of(9).Servers);
    }

    [Fact]
    public async Task Clean_SaysSoWhenThereWasNoDnsBackup()
    {
        // Nothing to put back is not a failure: it is every machine that never ran a session.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(_control.Calls);
        Assert.Contains("no DNS backup", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("were put back", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Clean_ReportsAnInterfaceThatRefusedAndFails()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9"));
        _control.Refuse.Add(7);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, error);

        Assert.Equal(4, exitCode);
        Assert.Contains("DNS settings of 1 interfaces were put back", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 interfaces could not be put back", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Administrator", error.ToString(), StringComparison.Ordinal);

        // Kept, so a second run can put the refused one back.
        Assert.NotNull(_backups.Load());
    }

    [Fact]
    public async Task Clean_ReportsAnAdapterThatIsGoneWithoutFailing()
    {
        // An adapter no longer on the machine is not a refusal, and its copy is kept.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));
        _backups.Save(new DnsBackup(SavedAt, [Static(9, "9.9.9.9"), Dhcp(7, "192.168.1.1")]));
        _machine.Inactive["{someone-else}"] = 7;
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("DNS settings of 1 interfaces were put back", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "1 interfaces in the DNS backup are no longer on this machine", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("stay in the backup for when they return", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Manual fallback", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(["{guid-7}"], _backups.Load()!.Interfaces.Select(state => state.Guid));
    }

    [Fact]
    public void RestoreDns_WhileTheProductStays_CountsACopyOfAbsentAdaptersAsSomethingToReport()
    {
        // Install prints the clean's lines only under this flag, and one says they are gone.
        _backups.Save(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));
        _machine.Inactive["{someone-else}"] = 7;

        var result = Cli.Commands.CleanCommand.RestoreDns(OpenDnsRestore, _lock, TextWriter.Null, TextWriter.Null, serviceStopped: false);

        Assert.Equal(new Cli.Commands.CleanResult(0, RemovedSomething: true), result);
    }

    [Fact]
    public void RestoreDns_AfterTheServiceStopped_WithNothingPutBackRemovedNothing()
    {
        // Uninstall says "put back once more" only when something was.
        _backups.Save(new DnsBackup(SavedAt, [Dhcp(7, "192.168.1.1")]));
        _machine.Inactive["{someone-else}"] = 7;

        var result = Cli.Commands.CleanCommand.RestoreDns(OpenDnsRestore, _lock, TextWriter.Null, TextWriter.Null, serviceStopped: true);

        Assert.Equal(new Cli.Commands.CleanResult(0, RemovedSomething: false), result);
    }

    [Fact]
    public void RestoreDns_AfterTheServiceStopped_PointsAnAdapterThatIsGoneAtTheManualFallback()
    {
        // Uninstall's own restore: once the product is gone nothing puts it back by itself.
        TakenOver(Static(9, "9.9.9.9"));
        _backups.Save(new DnsBackup(SavedAt, [Static(9, "9.9.9.9"), Dhcp(7, "192.168.1.1")]));
        _machine.Inactive["{someone-else}"] = 7;
        var output = new StringWriter();
        var error = new StringWriter();

        var result = Cli.Commands.CleanCommand.RestoreDns(OpenDnsRestore, _lock, output, error, serviceStopped: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1 interfaces in the DNS backup are no longer on this machine", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Manual fallback", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("for when they return", output.ToString() + error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_SaysNothingAboutAbsentAdaptersWhenNoneAreAbsent()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));
        var output = new StringWriter();

        await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.DoesNotContain("no longer on this machine", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_ReportsARestoreThatCouldNotBeginAsAFailure()
    {
        File.WriteAllText(HostsPath, HostsBlock.Write(string.Empty, ["0.0.0.0 example.com"]));
        DnsThrows = new UnauthorizedAccessException("Access to the registry key is denied.");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, error);

        Assert.Equal(4, exitCode);
        Assert.Contains("DNS settings could not be put back", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Access to the registry key is denied.", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Administrator", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(string.Empty, File.ReadAllText(HostsPath));
        Assert.Equal(1, _engine.RemoveEverythingCalls);
    }

    [Fact]
    public async Task Clean_PutsTheResolversBackEvenWhenTheFiltersCouldNotBeRemoved()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));
        EngineThrows = new InvalidOperationException("FwpmEngineOpen0 failed with 0x80070005.");

        var exitCode = await CleanAsync(new HostsFile(HostsPath), TextWriter.Null, TextWriter.Null);

        Assert.Equal(4, exitCode);
        Assert.Equal(["9.9.9.9"], _machine.Of(9).Servers);
    }

    [Fact]
    public async Task Clean_CountsPuttingTheResolversBackAsRemovingSomething()
    {
        // install prints what an earlier installation left only when this says so.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));

        var result = await Cli.Commands.CleanCommand.CleanAsync(
            new HostsFile(HostsPath), CreateEngine, OpenDnsRestore, _lock, TextWriter.Null, TextWriter.Null);

        Assert.Equal(new Cli.Commands.CleanResult(0, RemovedSomething: true), result);
    }

    [Fact]
    public async Task Clean_OnAMachineWithNothingOnIt_RemovedNothing()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");

        var result = await Cli.Commands.CleanCommand.CleanAsync(
            new HostsFile(HostsPath), CreateEngine, OpenDnsRestore, _lock, TextWriter.Null, TextWriter.Null);

        Assert.Equal(new Cli.Commands.CleanResult(0, RemovedSomething: false), result);
    }

    [Fact]
    public async Task Clean_WithARefusal_StillCountsWhatWasPutBack()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Dhcp(7, "192.168.1.1"), Static(9, "9.9.9.9"));
        _control.Refuse.Add(7);

        var result = await Cli.Commands.CleanCommand.CleanAsync(
            new HostsFile(HostsPath), CreateEngine, OpenDnsRestore, _lock, TextWriter.Null, TextWriter.Null);

        Assert.Equal(new Cli.Commands.CleanResult(4, RemovedSomething: true), result);
    }

    [Fact]
    public async Task Clean_WithARestoreThatCouldNotBegin_RemovedNothing()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        DnsThrows = new UnauthorizedAccessException("Access to the registry key is denied.");

        var result = await Cli.Commands.CleanCommand.CleanAsync(
            new HostsFile(HostsPath), CreateEngine, OpenDnsRestore, _lock, TextWriter.Null, TextWriter.Null);

        Assert.Equal(new Cli.Commands.CleanResult(4, RemovedSomething: false), result);
    }

    [Fact]
    public async Task Clean_CountsTheRefusedApartFromThePutBack()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(7, "7.7.7.7"), Static(8, "8.8.8.8"), Static(9, "9.9.9.9"));
        _control.Refuse.Add(8);
        var output = new StringWriter();
        var error = new StringWriter();

        await CleanAsync(new HostsFile(HostsPath), output, error);

        Assert.Contains("The DNS settings of 2 interfaces were put back.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("The DNS settings of 1 interfaces could not be put back.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_SaysARunningServiceTakesTheInterfacesAgain()
    {
        // As for the hosts block: the service saves its copy again and re-takes them.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));
        var output = new StringWriter();

        await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.Contains("next pass takes the interfaces again", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_SaysNothingAboutTheServiceWhenNoInterfaceWentBack()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));
        _control.Refuse.Add(9);
        var output = new StringWriter();

        await CleanAsync(new HostsFile(HostsPath), output, TextWriter.Null);

        Assert.DoesNotContain("next pass takes the interfaces again", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreDns_AfterTheServiceStopped_SaysNothingAboutItsNextPass()
    {
        TakenOver(Static(9, "9.9.9.9"));
        var output = new StringWriter();

        var result = Cli.Commands.CleanCommand.RestoreDns(OpenDnsRestore, _lock, output, TextWriter.Null, serviceStopped: true);

        Assert.Equal(new Cli.Commands.CleanResult(0, RemovedSomething: true), result);
        Assert.Contains("1 interfaces were put back", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("next pass", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_WhileTheServiceHoldsTheSettings_TouchesNoInterfaceAndFails()
    {
        // Nothing changed, rather than a restore a service pass can interleave with.
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));
        _lock.HeldElsewhere = true;
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CleanAsync(new HostsFile(HostsPath), output, error);

        Assert.Equal(4, exitCode);
        Assert.Empty(_control.Calls);
        Assert.NotNull(_backups.Load());
        Assert.Contains("kept them busy for 10 seconds", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("were put back", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_WhileTheServiceHoldsTheSettings_RemovedNothing()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));
        _lock.HeldElsewhere = true;

        var result = await Cli.Commands.CleanCommand.CleanAsync(
            new HostsFile(HostsPath), CreateEngine, OpenDnsRestore, _lock, TextWriter.Null, TextWriter.Null);

        Assert.Equal(new Cli.Commands.CleanResult(4, RemovedSomething: false), result);
    }

    [Fact]
    public async Task Clean_WaitsForTheSettingsNoLongerThanRecoverCanAfford()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");

        await CleanAsync(new HostsFile(HostsPath), TextWriter.Null, TextWriter.Null);

        Assert.Equal([Cli.Commands.CleanCommand.DnsLockWait], _lock.Waits);
        Assert.Equal(TimeSpan.FromSeconds(10), Cli.Commands.CleanCommand.DnsLockWait);
    }

    [Fact]
    public async Task Clean_HoldsTheSettingsWhileItPutsTheInterfacesBackAndLetsGoAfter()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(7, "7.7.7.7"), Static(9, "9.9.9.9"));
        var held = new List<bool>();
        _control.Before = _ => held.Add(_lock.Held);

        await CleanAsync(new HostsFile(HostsPath), TextWriter.Null, TextWriter.Null);

        Assert.Equal([true, true], held);
        Assert.False(_lock.Held);
    }

    [Fact]
    public async Task Clean_StillHoldsTheSettingsWhileTheCopyIsTakenAway()
    {
        File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        TakenOver(Static(9, "9.9.9.9"));
        bool? heldAtClear = null;
        _mirror.BeforeClear = () => heldAtClear = _lock.Held;

        await CleanAsync(new HostsFile(HostsPath), TextWriter.Null, TextWriter.Null);

        Assert.True(heldAtClear);
    }

    private Task<int> CleanAsync(HostsFile file, TextWriter output, TextWriter error) =>
        Cli.Commands.CleanCommand.RunAsync(file, CreateEngine, OpenDnsRestore, _lock, output, error);

    private IWfpEngine CreateEngine() => EngineThrows is { } failure ? throw failure : _engine;

    private DnsRestore OpenDnsRestore() => DnsThrows is { } failure
        ? throw failure
        : new DnsRestore(_backups, _control, _machine, NullLogger<DnsRestore>.Instance);

    /// <summary>The machine a dead service left: each interface on 127.0.0.1 and its first server, and the backup in both places.</summary>
    private void TakenOver(params InterfaceDnsState[] original)
    {
        foreach (var state in original)
        {
            _machine.Add(state.Guid, state.Index, isDhcp: false, ["127.0.0.1", .. state.Servers]);
        }

        _backups.Save(new DnsBackup(SavedAt, original));
    }

    private static InterfaceDnsState Dhcp(int index, params string[] servers) =>
        new($"{{guid-{index}}}", index, $"Adapter {index}", IsDhcp: true, servers);

    private static InterfaceDnsState Static(int index, params string[] servers) =>
        new($"{{guid-{index}}}", index, $"Adapter {index}", IsDhcp: false, servers);
}
