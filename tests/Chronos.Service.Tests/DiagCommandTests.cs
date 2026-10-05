using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Chronos.Cli.Commands;
using Chronos.Ipc;
using Chronos.Service.Apps;
using Chronos.Service.Configuration;
using Chronos.Service.Diagnostics;
using Chronos.Service.Dns;
using Chronos.Service.Setup;
using Chronos.Service.Wfp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

/// <summary>
/// The diagnostic package, against a machine made of fakes. Every source the command reads arrives
/// through one record, so a machine where every source refuses is a two-line arrangement. The
/// archives are real files under a directory this test owns.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiagCommandTests : IDisposable
{
    /// <summary>Documentation range (RFC 5737): nothing routed anywhere.</summary>
    private const string SiteAddress = "93.184.216.34";

    private const string Domain = "example.com";

    /// <summary>A second site rule, so the two counters of <c>[rules]</c> differ and a swap is visible.</summary>
    private const string OtherDomain = "example.net";

    private static readonly DateTimeOffset Collected = new(2026, 8, 30, 14, 5, 9, TimeSpan.Zero);

    /// <summary>When the DNS backup in these tests was taken.</summary>
    private static readonly DateTimeOffset BackedUp = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-diag-" + Guid.NewGuid().ToString("N"));

    private readonly ServiceTestClock _clock = new(Collected);
    private readonly FakeServiceRegistration _services = new();
    private readonly FakeTaskRegistration _tasks = new();
    private readonly FakeSystemEventLog _log = new();
    private readonly FakeWfpEngine _engine = new();
    private readonly ChronosPaths _paths;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    /// <summary>The machine this command exists for: every source refuses.</summary>
    private bool _everythingFails;

    /// <summary>A code the service refuses the status with, when it answers but will not say.</summary>
    private string? _refusedWith;

    /// <summary>An answer of the test's own making, for the ones the service would never write.</summary>
    private Func<Task<IpcResponse>>? _answerWith;

    private IReadOnlyList<string>? _hostsBlock = [$"0.0.0.0 {Domain}", $"0.0.0.0 www.{Domain}"];

    private PortUse _port = PortUse.HeldBy("dnscache (process 1234)");

    private IReadOnlyList<InterfaceDns> _dns = [new InterfaceDns("Ethernet", "Ethernet", ["1.1.1.1", "1.0.0.1"])];

    private LoopbackPort _loopback = new(Free: true, Holder: null);

    /// <summary>Every port the command tried to take.</summary>
    private readonly List<int> _probed = [];

    private readonly MemoryMirror _mirror = new();
    private readonly DnsBackupStore _backups;

    private bool _probeThrows;

    private bool _backupsThrow;

    public DiagCommandTests()
    {
        _paths = new ChronosPaths(Path.Combine(_root, "data"), Path.Combine(_root, "user"));
        _paths.EnsureDataDirectoryExists();
        _backups = new DnsBackupStore(_paths, _mirror, NullLogger<DnsBackupStore>.Instance);

        // The machine a package is collected from: configured and has run, so there is a configuration, a state file and a day of logs.
        File.WriteAllText(_paths.ConfigFile, $$"""{"sites":[{"domain":"{{Domain}}"}]}""");
        File.WriteAllText(_paths.StateFile, """{"id":"9f0a"}""");
        File.WriteAllText(Path.Combine(_paths.ServiceLogDirectory, "service-20260830.log"), "started\r\n");

        _services.State = ServiceRunState.Running;
        _tasks.Registered = true;
        _engine.Seed($"Chronos block {SiteAddress}");
        _engine.Seed($"Chronos block {SiteAddress} udp/443");
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Diag_WritesAnArchiveAndPrintsItsPath()
    {
        var exit = await DiagCommand.RunAsync(Context(), verbose: false, _clock, _output, _error, CancellationToken.None);

        var path = _output.ToString().Trim();

        Assert.Equal(0, exit);
        Assert.True(File.Exists(path), path);
    }

    [Fact]
    public async Task Diag_PrintsTheArchivePathAndNothingElse()
    {
        // The path must be in the console: someone piping this into a file must not take the wrong line out of a page of progress.
        await DiagCommand.RunAsync(Context(), verbose: false, _clock, _output, _error, CancellationToken.None);

        Assert.Single(Lines(_output));
        Assert.Empty(Lines(_error));
    }

    [Fact]
    public async Task Diag_ContainsEverythingTheRequirementLists()
    {
        using var archive = ZipFile.OpenRead(await ArchiveAsync());

        Assert.NotNull(archive.GetEntry("config.json"));
        Assert.NotNull(archive.GetEntry("state.json"));
        Assert.NotNull(archive.GetEntry("logs/service-20260830.log"));

        var report = Read(archive, "report.txt");
        foreach (var section in new[]
                 {
                     "versions", "files", "logs", "filters", "hosts", "port-53", "dns", "dns-l2",
                     "service", "task", "layers", "rules", "app-event-source",
                 })
        {
            Assert.Contains($"[{section}]", report, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Diag_CarriesTheConfigurationAndTheStateWholeIntoTheArchive()
    {
        // The one place the whole configuration is allowed to be: an administrator asked for this package.
        using var archive = ZipFile.OpenRead(await ArchiveAsync());

        Assert.Contains(Domain, Read(archive, "config.json"), StringComparison.Ordinal);
        Assert.Contains("9f0a", Read(archive, "state.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_OnAMachineWhereNothingWorks_StillProducesAnArchive()
    {
        // Every source refuses: no service, engine, configuration file, scheduler or logs. The command must not stop at the first absent source.
        BreakEverything();

        var exit = await DiagCommand.RunAsync(Context(), verbose: false, _clock, _output, _error, CancellationToken.None);

        Assert.Equal(0, exit);

        using var archive = ZipFile.OpenRead(_output.ToString().Trim());
        var report = Read(archive, "report.txt");

        Assert.Contains("the service did not answer", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_OnAMachineWhereNothingWorks_SaysWhyEachSectionIsEmpty()
    {
        // Not just "the package was produced" but "still worth reading": each refusal is written where the answer would have been.
        BreakEverything();

        using var archive = ZipFile.OpenRead(await ArchiveAsync());
        var report = Read(archive, "report.txt");

        Assert.Contains("FwpmEngineOpen0", report, StringComparison.Ordinal);
        Assert.Contains("The hosts file is held open", report, StringComparison.Ordinal);
        Assert.Contains("The service manager is not available", report, StringComparison.Ordinal);
        Assert.Contains("The task scheduler did not answer", report, StringComparison.Ordinal);
        Assert.Contains("could not be determined", report, StringComparison.Ordinal);
        Assert.Contains("config.json  absent", report, StringComparison.Ordinal);
        Assert.Contains("state.json  absent", report, StringComparison.Ordinal);
    }

    /// <summary>The package is read in English, so a refusal's code is written out in the CLI's words.</summary>
    [Fact]
    public async Task Diag_WritesTheServicesRefusalInWords()
    {
        _refusedWith = IpcCodes.RequestEmpty;

        var report = await ReportAsync();

        Assert.Contains("the service did not answer: Request is empty.", SectionOf(report, "layers"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_SaysSoWhenTheServiceAnswersWithNothingAtAll()
    {
        // The client turns a JSON null into an exception, but the seam under it is a delegate; a null
        // from it is a service that answered nothing, not a reason to lose two sections.
        _answerWith = () => Task.FromResult<IpcResponse>(null!);

        var report = await ReportAsync();

        // The exact sentence: a null taken for a response would fall to the catch-all and say "Object reference not set", which begins with the same words.
        Assert.Contains("the service did not answer: it answered without a status", SectionOf(report, "layers"), StringComparison.Ordinal);
        Assert.Contains("the service did not answer: it answered without a status", SectionOf(report, "rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_SaysSoWhenTheServicesAnswerCannotBeParsed()
    {
        _answerWith = () => Task.FromException<IpcResponse>(new System.Text.Json.JsonException("Unexpected end."));

        var report = await ReportAsync();

        Assert.Contains("the service did not answer: Unexpected end.", SectionOf(report, "layers"), StringComparison.Ordinal);
        Assert.Contains("the service did not answer: Unexpected end.", SectionOf(report, "rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_SaysSoWhenTheServiceAcceptsButSendsNoStatus()
    {
        _answerWith = () => Task.FromResult(new IpcResponse { Accepted = true });

        var report = await ReportAsync();

        Assert.Contains("the service did not answer: it answered without a status", SectionOf(report, "layers"), StringComparison.Ordinal);
        Assert.Contains("the service did not answer: it answered without a status", SectionOf(report, "rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_SaysSoWhenTheStatusIsMissingItsLists()
    {
        // A status that parsed but has no layers list is not one with no layers ("has not reported yet"); it must not crash the section.
        _answerWith = () => Task.FromResult(new IpcResponse
        {
            Accepted = true,
            Status = new StatusPayload("Active", Collected, null, null, null, 15, null!, null!, null!),
        });

        var report = await ReportAsync();

        Assert.Contains("the service did not answer: it answered with a status this program cannot read", SectionOf(report, "layers"), StringComparison.Ordinal);
        Assert.Contains("the service did not answer: it answered with a status this program cannot read", SectionOf(report, "rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_SaysSoWhenAListInTheStatusHoldsANull()
    {
        // The verbose rules section walks every element; a null in one must not crash it.
        _answerWith = () => Task.FromResult(new IpcResponse
        {
            Accepted = true,
            Status = new StatusPayload("Active", Collected, null, null, null, 15, [null!], [], []),
        });

        var report = await ReportAsync(verbose: true);

        Assert.Contains("the service did not answer: it answered with a status this program cannot read", SectionOf(report, "rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_SaysTheServiceWasSilentInEachSectionThatNeededIt()
    {
        // Both sections, asked separately: they print the same sentence when there is no answer, so a
        // silent [layers] would otherwise go unnoticed.
        BreakEverything();

        var report = await ReportAsync();

        Assert.Contains("the service did not answer", SectionOf(report, "layers"), StringComparison.Ordinal);
        Assert.Contains("the service did not answer", SectionOf(report, "rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_NamesTheVersionOfEveryComponentAndOfWindowsItself()
    {
        // The component versions and the operating system version are the first thing a bug report reader
        // needs; an empty [versions] or an OS printed as nothing reads as a machine with no product.
        var versions = SectionOf(await ReportAsync(), "versions");

        foreach (var component in new[] { "chronos", "Chronos.Service", "Chronos.Ipc", "Chronos.Core" })
        {
            Assert.NotEmpty(ValueOf(versions, component));
        }

        Assert.Equal(RuntimeInformation.OSDescription, ValueOf(versions, "windows"));
        Assert.NotEmpty(ValueOf(versions, "windows"));
        Assert.NotEmpty(ValueOf(versions, "architecture"));
        Assert.NotEmpty(ValueOf(versions, "runtime"));
        Assert.Equal(
            IpcProtocol.Version.ToString(CultureInfo.InvariantCulture), ValueOf(versions, "ipc protocol"));
    }

    [Fact]
    public async Task Diag_OnAMachineWithNoLogsAtAll_StillCarriesTheFolder()
    {
        // The archive shape must not depend on the machine: a reader who finds no logs folder cannot tell a
        // missing directory from a package this command failed to finish.
        BreakEverything();

        using var archive = ZipFile.OpenRead(await ArchiveAsync());

        Assert.NotNull(archive.GetEntry("logs/"));
        Assert.Contains("does not exist", Read(archive, "report.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_OnAMachineWhoseLogDirectoryIsEmpty_StillCarriesTheFolder()
    {
        // The ordinary way to have no logs: a fresh installation whose service has not started has the
        // directory and nothing in it. The archive shape must hold here too.
        foreach (var path in Directory.GetFiles(_paths.ServiceLogDirectory))
        {
            File.Delete(path);
        }

        using var archive = ZipFile.OpenRead(await ArchiveAsync());

        Assert.NotNull(archive.GetEntry("logs/"));
        Assert.Contains("0 log files were copied", Read(archive, "report.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_CopiesALogTheServiceIsStillWritingTo()
    {
        // The service holds open the log of the day an incident happened; anything stricter than a shared
        // read would report exactly that file as one that could not be copied.
        var busy = Path.Combine(_paths.ServiceLogDirectory, "service-20260831.log");
        await using var held = new FileStream(busy, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await held.WriteAsync("in progress\r\n"u8.ToArray());
        await held.FlushAsync();

        using var archive = ZipFile.OpenRead(await ArchiveAsync());

        Assert.NotNull(archive.GetEntry("logs/service-20260831.log"));
        Assert.Contains("2 log files were copied", Read(archive, "report.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_DoesNotCallSomethingThatIsNotAFileAnAbsentOne()
    {
        // File.Exists answers false for anything that is not a file (a directory of that name included),
        // which would send the reader off to configure a machine with something else in the way. The file
        // is opened rather than asked about, so the report carries what the machine says.
        File.Delete(_paths.ConfigFile);
        Directory.CreateDirectory(_paths.ConfigFile);

        var files = SectionOf(await ReportAsync(), "files");

        Assert.Contains("config.json  could not be copied", files, StringComparison.Ordinal);
        Assert.DoesNotContain("config.json  absent", files, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_TellsALogDirectoryItCouldNotListFromOneThatIsNotThere()
    {
        // Directory.Exists answers false for everything that is not a readable directory, which would read
        // as "the service has never run here". On a directory that exists but will not be listed it answers
        // true and the enumeration raises, losing the whole package.
        //
        // A file standing where the directory goes is the shape of that failure a test can induce on any
        // machine; a Deny ACE cannot be arranged from a test host that reads past it.
        Directory.Delete(_paths.ServiceLogDirectory, recursive: true);
        File.WriteAllText(_paths.ServiceLogDirectory, string.Empty);

        var report = await ReportAsync();

        Assert.Contains("could not be listed", SectionOf(report, "logs"), StringComparison.Ordinal);
        Assert.DoesNotContain("does not exist", SectionOf(report, "logs"), StringComparison.Ordinal);

        // The fallback is not declared absent on the strength of a directory nobody could read: "no
        // record" and "could not look" send a reader to opposite places.
        var source = SectionOf(report, "app-event-source");

        Assert.Contains("could not be read", source, StringComparison.Ordinal);
        Assert.DoesNotContain("record no fall back", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_WithoutVerbose_DoesNotListTheAddressesThemselves()
    {
        using var archive = ZipFile.OpenRead(await ArchiveAsync(verbose: false));
        var report = Read(archive, "report.txt");

        Assert.DoesNotContain(SiteAddress, report, StringComparison.Ordinal);
        Assert.DoesNotContain(Domain, report, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherDomain, report, StringComparison.Ordinal);

        Assert.Contains("2 address filters", report, StringComparison.Ordinal);
        Assert.Contains("the Chronos block is present: 2 lines", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_CountsTheSiteRulesAndTheApplicationRulesApart()
    {
        // Two sites and one application, so the sentence is wrong if the counters are read in the other
        // order. A site rule not in force is a domain still resolving, an application rule a program
        // still starting.
        var rules = SectionOf(await ReportAsync(), "rules");

        Assert.Contains("2 site rules and 1 application rules are in force", rules, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_WithVerbose_ListsTheAddressesTheBlockAndTheRules()
    {
        using var archive = ZipFile.OpenRead(await ArchiveAsync(verbose: true));
        var report = Read(archive, "report.txt");

        Assert.Contains($"Chronos block {SiteAddress}", report, StringComparison.Ordinal);
        Assert.Contains($"0.0.0.0 {Domain}", report, StringComparison.Ordinal);
        Assert.Contains($"site  {Domain}", report, StringComparison.Ordinal);
        Assert.Contains(@"app   Path  C:\Games\game.exe", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_WritesNoPathAndNoDomainIntoTheLog()
    {
        // The archive holds the paths and the domains; the record of having built it holds neither.
        await DiagCommand.RunAsync(Context(), verbose: true, _clock, _output, _error, CancellationToken.None);

        var entry = Assert.Single(_log.Entries);

        Assert.Equal(SystemEventLevel.Information, entry.Level);
        Assert.Equal(ChronosEvents.DiagnosticsCollected, entry.EventId);
        Assert.DoesNotContain(_paths.DataDirectory, entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_output.ToString().Trim(), entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Domain, entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SiteAddress, entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "dnscache (process 1234)", "UDP port 53 is held by dnscache (process 1234)")]
    [InlineData(true, null, "nothing on this machine is bound to UDP port 53")]
    [InlineData(false, null, "the owner of UDP port 53 could not be determined")]
    public async Task Diag_TellsAPortNobodyHoldsFromAPortItCouldNotRead(bool known, string? holder, string expected)
    {
        // Three answers, not two: "nobody holds it" sends the reader to the DNS layer, "this machine would
        // not say" to their own rights.
        _port = new PortUse(known, holder);

        using var archive = ZipFile.OpenRead(await ArchiveAsync());

        Assert.Contains(expected, Read(archive, "report.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_ReportsTheDnsSettingsOfEveryActiveInterface()
    {
        _dns = [new InterfaceDns("Wi-Fi", "Wireless80211", ["9.9.9.9"]), new InterfaceDns("Ethernet", "Ethernet", [])];

        using var archive = ZipFile.OpenRead(await ArchiveAsync());
        var report = Read(archive, "report.txt");

        Assert.Contains("Wi-Fi (Wireless80211): 9.9.9.9", report, StringComparison.Ordinal);
        Assert.Contains("Ethernet (Ethernet): no resolvers configured", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, null, "free: the resolver could take it")]
    [InlineData(false, "dnscache (process 1234)", "taken by dnscache (process 1234)")]
    [InlineData(false, null, "taken, by a process this machine would not name")]
    public async Task DnsL2_SaysWhetherTheResolverCouldTakeItsPort(bool free, string? holder, string expected)
    {
        _loopback = new LoopbackPort(free, holder);

        var section = SectionOf(await ReportAsync(), "dns-l2");

        Assert.Equal(expected, ValueOf(section, "127.0.0.1:53"));
    }

    [Fact]
    public async Task DnsL2_TriesPort53AndNothingElse()
    {
        // A probe that binds and lets go; the resolver itself is never started from here.
        await ReportAsync();

        Assert.Equal([53], _probed);
    }

    [Theory]
    [InlineData(true, true, "in the file and in the registry")]
    [InlineData(true, false, "in the file only")]
    [InlineData(false, true, "in the registry only")]
    [InlineData(false, false, "in neither place: there is nothing to restore")]
    public async Task DnsL2_SaysWhichPlacesHoldACopy(bool file, bool registry, string expected)
    {
        _backups.Save(new DnsBackup(BackedUp, [Static("{guid-9}", 9, "Ethernet", "9.9.9.9")]));
        if (!file)
        {
            File.Delete(_paths.DnsBackupFile);
        }

        if (!registry)
        {
            _mirror.Held = null;
        }

        var section = SectionOf(await ReportAsync(), "dns-l2");

        Assert.Equal(expected, ValueOf(section, "backup"));
    }

    [Fact]
    public async Task DnsL2_DescribesEachCopyAndWhatARestoreWouldPutBack()
    {
        _backups.Save(new DnsBackup(
            BackedUp,
            [
                Static("{guid-9}", 9, "Ethernet", "9.9.9.9", "1.1.1.1"),
                new InterfaceDnsState("{guid-7}", 7, "Wi-Fi", IsDhcp: true, ["192.168.1.1"]),
            ]));

        // The registry has a newer copy of one interface: a restore would use that one.
        _mirror.Held = """{"savedAt":"2026-08-30T13:00:00+00:00","interfaces":[{"guid":"{guid-3}","index":3,"name":"Ethernet 3","isDhcp":true,"servers":[]}]}""";

        var section = SectionOf(await ReportAsync(), "dns-l2");

        Assert.Equal("saved 2026-08-30 12:00:00 UTC, 2 interfaces", ValueOf(section, "file"));
        Assert.Equal("saved 2026-08-30 13:00:00 UTC, 1 interfaces", ValueOf(section, "registry"));
        Assert.Equal("the registry copy", ValueOf(section, "restore from"));
        Assert.Contains("Ethernet 3: DHCP", section, StringComparison.Ordinal);
        Assert.DoesNotContain("Wi-Fi: DHCP", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DnsL2_ListsAStaticCopyWithItsServersInOrder()
    {
        _backups.Save(new DnsBackup(BackedUp, [Static("{guid-9}", 9, "Ethernet", "9.9.9.9", "1.1.1.1")]));

        var section = SectionOf(await ReportAsync(), "dns-l2");

        Assert.Equal("the file copy", ValueOf(section, "restore from"));
        Assert.Contains("Ethernet: static 9.9.9.9, 1.1.1.1", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DnsL2_SaysSoWhenAPlaceHoldsNoUsableCopy()
    {
        _mirror.Held = "{ this is not json";

        var section = SectionOf(await ReportAsync(), "dns-l2");

        Assert.Equal("no usable copy", ValueOf(section, "file"));
        Assert.Equal("no usable copy", ValueOf(section, "registry"));
        Assert.DoesNotContain("restore from", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DnsL2_NamesTheActiveInterfacesThatPointAtTheLoopback()
    {
        _dns =
        [
            new InterfaceDns("Wi-Fi", "Wireless80211", ["127.0.0.1", "192.168.1.1"]),
            new InterfaceDns("Ethernet", "Ethernet", ["1.1.1.1"]),
            new InterfaceDns("Ethernet 2", "Ethernet", ["9.9.9.9", "127.0.0.1"]),
        ];

        var section = SectionOf(await ReportAsync(), "dns-l2");

        Assert.Equal("Wi-Fi, Ethernet 2", ValueOf(section, "on 127.0.0.1"));
    }

    [Fact]
    public async Task DnsL2_SaysSoWhenNoInterfacePointsAtTheLoopback()
    {
        var section = SectionOf(await ReportAsync(), "dns-l2");

        Assert.Equal("no active interface", ValueOf(section, "on 127.0.0.1"));
    }

    [Fact]
    public async Task DnsL2_ReportsEachPartThatCouldNotBeReadWhereItsAnswerWouldBe()
    {
        BreakEverything();

        var section = SectionOf(await ReportAsync(), "dns-l2");

        Assert.StartsWith("could not be read: SocketException", ValueOf(section, "127.0.0.1:53"), StringComparison.Ordinal);
        Assert.StartsWith("could not be read: UnauthorizedAccessException", ValueOf(section, "backup"), StringComparison.Ordinal);
        Assert.StartsWith("could not be read: InvalidOperationException", ValueOf(section, "on 127.0.0.1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DnsL2_APortThatCouldNotBeTriedDoesNotCostTheRestOfTheSection()
    {
        _probeThrows = true;
        _dns = [new InterfaceDns("Wi-Fi", "Wireless80211", ["127.0.0.1", "192.168.1.1"])];

        var section = SectionOf(await ReportAsync(), "dns-l2");

        Assert.Equal("in neither place: there is nothing to restore", ValueOf(section, "backup"));
        Assert.Equal("Wi-Fi", ValueOf(section, "on 127.0.0.1"));
    }

    [Fact]
    public async Task DnsL2_NamesNoDomainEvenWhenVerbose()
    {
        // A report about the machine, not about where anybody went.
        _backups.Save(new DnsBackup(BackedUp, [Static("{guid-9}", 9, "Ethernet", "9.9.9.9")]));

        var section = SectionOf(await ReportAsync(verbose: true), "dns-l2");

        Assert.DoesNotContain(Domain, section, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OtherDomain, section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diag_LeavesTheDnsBackupAsItFoundIt()
    {
        _backups.Save(new DnsBackup(BackedUp, [Static("{guid-9}", 9, "Ethernet", "9.9.9.9")]));
        var file = File.ReadAllText(_paths.DnsBackupFile);
        var registry = _mirror.Held;
        var writes = _mirror.Writes;

        await ReportAsync();

        Assert.Equal(file, File.ReadAllText(_paths.DnsBackupFile));
        Assert.Equal(registry, _mirror.Held);
        Assert.Equal(writes, _mirror.Writes);
    }

    [Fact]
    public async Task Diag_ReportsTheServiceTheTaskAndTheLayers()
    {
        _services.State = ServiceRunState.Stopped;
        _tasks.Registered = false;

        using var archive = ZipFile.OpenRead(await ArchiveAsync());
        var report = Read(archive, "report.txt");

        Assert.Contains("the service manager reports: Stopped", report, StringComparison.Ordinal);
        Assert.Contains("the recovery task is not registered", report, StringComparison.Ordinal);
        Assert.Contains("wfp  unavailable: wfp.engine-unavailable", report, StringComparison.Ordinal);
        Assert.Contains("hosts  available", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_WritesAPlatformFailureAsItsNumberAndNotAsTheSentenceWindowsWrote()
    {
        // The product's output is English and Windows's is not (Win32Exception(5).Message is localised on
        // non-English machines), and this report is pasted into tickets. The number is the same everywhere.
        _services.QueryThrows = new Win32Exception(5);

        var service = SectionOf(await ReportAsync(), "service");

        Assert.Contains("0x00000005", service, StringComparison.Ordinal);
        Assert.DoesNotContain(new Win32Exception(5).Message, service, StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_ReadsThroughTheWrapperTheServiceManagerPutsItIn()
    {
        // System.ServiceProcess wraps its Win32 failures, so the localised sentence arrives one level
        // down and a check of the outer type alone would let it through.
        var wrapped = new InvalidOperationException(
            "Cannot open Service Control Manager.", new Win32Exception(5));

        var explained = DiagCommand.Explain(wrapped);

        Assert.Contains("InvalidOperationException", explained, StringComparison.Ordinal);
        Assert.Contains("0x00000005", explained, StringComparison.Ordinal);
        Assert.DoesNotContain(new Win32Exception(5).Message, explained, StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_KeepsTheSentencesTheProductWroteItself()
    {
        // Only the platform's are dropped. Our own are already English and say what happened.
        var explained = DiagCommand.Explain(new IOException("The hosts file is held open."));

        Assert.Equal("IOException: The hosts file is held open.", explained);
    }

    [Fact]
    public async Task Diag_ReportsTheFallbackEventSourceOutOfTheLogs()
    {
        // The fallback costs the full-path rules and shows up in no layer status, so the logged sentence
        // is the only place the package can find it.
        File.WriteAllText(
            Path.Combine(_paths.ServiceLogDirectory, "service-20260829.log"),
            $"2026-08-29 09:00:00.000 +03:00 [INF] {ProcessEventsFactory.FellBackToWmi}\r\n");

        var source = SectionOf(await ReportAsync(), "app-event-source");

        Assert.Contains("fell back to the WMI process source", source, StringComparison.Ordinal);
        Assert.Contains("2026-08-29 09:00:00.000", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_DatesTheFallbackAndSpeaksOfItInThePastTense()
    {
        // The search covers everything retention left, so the newest match may be days old. A machine that
        // fell back on Saturday and restarted cleanly twice is not on the fallback, and telling an
        // administrator otherwise sends them to rewrite working rules. The moment makes the line answerable.
        File.WriteAllText(
            Path.Combine(_paths.ServiceLogDirectory, "service-20260829.log"),
            $"2026-08-29 09:00:00.000 +03:00 [INF] {ProcessEventsFactory.FellBackToWmi}\r\n");

        var source = SectionOf(await ReportAsync(), "app-event-source");

        // Read off the line and restated in UTC, like every other moment in the report.
        Assert.Contains("at 2026-08-29 06:00:00 UTC", source, StringComparison.Ordinal);
        Assert.Contains("not a statement about this run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot match", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_OnAMachineThatNeverFellBack_SaysSo()
    {
        using var archive = ZipFile.OpenRead(await ArchiveAsync());

        Assert.Contains("record no fall back", Read(archive, "report.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_DoesNotReadAnOrdinaryLogLineAsTheFallback()
    {
        // The other end of the pin: the fallback is found by one sentence, and a looser search (a level
        // marker, the word "WMI" in another sentence) would report every machine as running blind.
        File.WriteAllText(
            Path.Combine(_paths.ServiceLogDirectory, "service-20260828.log"),
            "2026-08-28 09:00:00.000 +03:00 [INF] The kernel trace session started.\r\n"
            + "2026-08-28 09:00:01.000 +03:00 [INF] The WMI process source is available.\r\n"
            + "2026-08-28 09:00:02.000 +03:00 [WRN] falling back to the hosts layer.\r\n");

        var source = SectionOf(await ReportAsync(), "app-event-source");

        Assert.Contains("record no fall back", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fell back to the WMI process source at", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_ReportsAnArchiveThatCouldNotBeWritten()
    {
        // A file where the folder has to go: the one failure that cannot be written into the package.
        File.WriteAllText(Path.Combine(_paths.DataDirectory, "diag"), string.Empty);

        var exit = await DiagCommand.RunAsync(Context(), verbose: false, _clock, _output, _error, CancellationToken.None);

        Assert.Equal(4, exit);
        Assert.Empty(Lines(_output));
        Assert.Contains("could not be written", _error.ToString(), StringComparison.Ordinal);

        // And no entry claiming a package that does not exist.
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Destination_NamesThePackageAfterTheMomentItWasCollected()
    {
        // Sortable and UTC: a time zone change between two runs would otherwise sort names backwards.
        var path = DiagCommand.Destination(_paths, Collected);

        Assert.Equal(Path.Combine(_paths.DataDirectory, "diag", "chronos-diag-20260830-140509.zip"), path);
    }

    [Fact]
    public void Destination_FallsBackToTheTemporaryDirectoryWhenThereIsNoDataDirectory()
    {
        // One shape "this machine is broken" takes; the package must still land where the caller can find it.
        var missing = new ChronosPaths(Path.Combine(_root, "gone"), Path.Combine(_root, "user"));

        var path = DiagCommand.Destination(missing, Collected);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "chronos-diag-20260830-140509.zip"), path);
    }

    private async Task<string> ArchiveAsync(bool verbose = false)
    {
        var exit = await DiagCommand.RunAsync(Context(), verbose, _clock, _output, _error, CancellationToken.None);

        Assert.Equal(0, exit);

        return _output.ToString().Trim();
    }

    /// <summary>The machine, as this command reads it. The three members that change something raise: diag takes nothing off this machine and creates nothing on it.</summary>
    private SetupContext Context() => new(
        _services,
        ServiceStatusAsync,
        _tasks,
        _log,
        new FakeAutostartEntry(),
        _paths,
        ServiceDefinition.Chronos(@"C:\Program Files\Chronos\Chronos.Service.exe"),
        @"C:\Program Files\Chronos\chronos.exe",
        (_, _) => throw new InvalidOperationException("Collecting diagnostics takes nothing off this machine."),
        () => throw new InvalidOperationException("Collecting diagnostics does not clear the session state."),
        _ => throw new InvalidOperationException("Collecting diagnostics does not create the data directory."),
        OpenEngine,
        ReadHostsBlock,
        _ => _port,
        DnsSettings,
        ProbeLoopbackPort,
        DnsBackups,
        (_, _) => throw new InvalidOperationException("Collecting diagnostics restores nothing."),
        () => throw new InvalidOperationException("Collecting diagnostics removes nothing."));

    private LoopbackPort ProbeLoopbackPort(int port)
    {
        _probed.Add(port);

        return _probeThrows
            ? throw new System.Net.Sockets.SocketException(10013)
            : _loopback;
    }

    private DnsBackupSources DnsBackups() => _backupsThrow
        ? throw new UnauthorizedAccessException("Access to the registry key is denied.")
        : _backups.Sources();

    private static InterfaceDnsState Static(string guid, int index, string name, params string[] servers) =>
        new(guid, index, name, IsDhcp: false, servers);

    private Task<IpcResponse> ServiceStatusAsync(CancellationToken ct) => _answerWith is { } make
        ? make()
        : _everythingFails
        ? Task.FromException<IpcResponse>(new IOException("The pipe is broken."))
        : _refusedWith is { } code
        ? Task.FromResult(IpcResponse.Fail(code))
        : Task.FromResult(IpcResponse.Ok(new StatusPayload(
            State: "Active",
            Now: Collected,
            StartedAt: Collected.AddHours(-1),
            EndsAt: Collected.AddHours(1),
            UnlockEffectiveAt: null,
            CoolDownMinutes: 15,
            Sites:
            [
                new SiteRuleMessage(Domain, IncludeSubdomains: true),
                new SiteRuleMessage(OtherDomain, IncludeSubdomains: false),
            ],
            Apps: [new AppRuleMessage("Path", @"C:\Games\game.exe")],
            Layers:
            [
                new LayerStatus("hosts", IsAvailable: true, ReasonCode: null, LastOutcome: "applied", Collected),
                new LayerStatus("wfp", IsAvailable: false, "wfp.engine-unavailable", "failed", Collected),
            ])));

    private IWfpEngine OpenEngine() => _everythingFails
        ? throw new InvalidOperationException("FwpmEngineOpen0 failed with 0x80320003.")
        : _engine;

    private IReadOnlyList<string>? ReadHostsBlock() => _everythingFails
        ? throw new IOException("The hosts file is held open by another process.")
        : _hostsBlock;

    private IReadOnlyList<InterfaceDns> DnsSettings() => _everythingFails
        ? throw new InvalidOperationException("The network interfaces could not be enumerated.")
        : _dns;

    /// <summary>Turns every source of the machine off at once and takes the files with it.</summary>
    private void BreakEverything()
    {
        _everythingFails = true;
        _services.QueryThrows = new InvalidOperationException("The service manager is not available.");
        _tasks.ExistsThrows = new InvalidOperationException("The task scheduler did not answer.");
        _port = PortUse.Unknown;
        _probeThrows = true;
        _backupsThrow = true;

        File.Delete(_paths.ConfigFile);
        File.Delete(_paths.StateFile);
        Directory.Delete(_paths.ServiceLogDirectory, recursive: true);
    }

    private async Task<string> ReportAsync(bool verbose = false)
    {
        using var archive = ZipFile.OpenRead(await ArchiveAsync(verbose));

        return Read(archive, "report.txt");
    }

    /// <summary>The body of one section, from its heading down to the next one.</summary>
    /// <remarks>
    /// Searching the whole report cannot tell sections apart: <c>[layers]</c> and <c>[rules]</c> both
    /// write "the service did not answer", so a whole-text assertion passes with either gone.
    /// </remarks>
    private static string SectionOf(string report, string name)
    {
        var heading = $"[{name}]";
        var start = report.IndexOf(heading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"The report has no {heading} section.{Environment.NewLine}{report}");

        start += heading.Length;

        var next = report.IndexOf($"{Environment.NewLine}[", start, StringComparison.Ordinal);

        return next < 0 ? report[start..] : report[start..next];
    }

    /// <summary>The value of one named row of a section, without the padding that lines them up.</summary>
    private static string ValueOf(string section, string name)
    {
        var prefix = $"  {name}";
        var row = section
            .Split(Environment.NewLine)
            .SingleOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal)
                && line.Length > prefix.Length
                && line[prefix.Length] == ' ');

        Assert.True(row is not null, $"No '{name}' row in:{Environment.NewLine}{section}");

        return row![prefix.Length..].Trim();
    }

    private static string Read(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);

        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open());

        return reader.ReadToEnd();
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
