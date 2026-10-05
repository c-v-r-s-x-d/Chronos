using System.IO.Compression;
using System.Runtime.Versioning;
using Chronos.Cli.Commands;
using Chronos.Ipc;
using Chronos.Service.Configuration;
using Chronos.Service.Dns;
using Chronos.Service.Setup;

// The test SDK generates its own Program in the global namespace and it wins the unqualified name; aliased because the class under test is the tool's entry point.
using CliProgram = Chronos.Cli.Program;

namespace Chronos.Service.Tests;

/// <summary>
/// The command line itself: what a flag means, what an unknown argument does, and behaviour without administrator rights
/// or on a machine the tool cannot place itself on. The rights check and the machine are delegates, so nothing reads the
/// real service manager or <c>%ProgramData%</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProgramTests : IDisposable
{
    private const string Domain = "example.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-prog-" + Guid.NewGuid().ToString("N"));

    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly ChronosPaths _paths;

    private int _machineReads;

    public ProgramTests()
    {
        _paths = new ChronosPaths(Path.Combine(_root, "data"), Path.Combine(_root, "user"));
        _paths.EnsureDataDirectoryExists();

        File.WriteAllText(_paths.ConfigFile, $$"""{"sites":[{"domain":"{{Domain}}"}]}""");
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Theory]
    [InlineData(new[] { "diag" }, true, false)]
    [InlineData(new[] { "diag", "--verbose" }, true, true)]
    [InlineData(new[] { "uninstall", "--purge" }, true, true)]
    [InlineData(new[] { "diag", "--purge" }, false, false)]
    [InlineData(new[] { "diag", "--verbse" }, false, false)]
    [InlineData(new[] { "diag", "--VERBOSE" }, false, false)]
    [InlineData(new[] { "diag", "--verbose", "extra" }, false, false)]
    [InlineData(new[] { "diag", "extra", "--verbose" }, false, false)]
    [InlineData(new[] { "diag", "--verbose", "--verbose" }, false, false)]
    [InlineData(new[] { "uninstall", "--purge", "--purge" }, false, false)]
    public void TryReadFlag_AcceptsTheOneFlagAndRejectsEverythingElse(string[] args, bool accepted, bool present)
    {
        // Ordinal and exhaustive. An ignored wrong flag teaches the user it works (--purge on a command that takes none, --verbse).
        // A flag given twice is also a typo, and acting on half of 'uninstall --purge --purge' decides for them.
        var flag = args[0] == "diag" ? "--verbose" : "--purge";

        Assert.Equal(accepted, CliProgram.TryReadFlag(args, flag, out var read));
        Assert.Equal(present, read);
    }

    [Fact]
    public async Task Diag_RejectsAnUnknownArgumentWithoutLookingAtTheMachine()
    {
        var exit = await CliProgram.DiagAsync(
            ["diag", "--everything"], Elevated, Machine, _output, _error, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("takes no arguments but --verbose", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _output.ToString());

        // Nothing was collected, so there is no archive and nothing was read off this machine.
        Assert.Equal(0, _machineReads);
    }

    [Fact]
    public async Task Diag_WithoutAdministratorRights_RefusesAndReadsNothing()
    {
        // Half of the package needs the rights; without them its filter section would say "access is denied" about a healthy machine, so the answer is a sentence.
        var exit = await CliProgram.DiagAsync(
            ["diag"], () => false, Machine, _output, _error, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("administrator rights", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _output.ToString());
        Assert.Equal(0, _machineReads);
    }

    [Fact]
    public async Task Diag_WithoutTheFlag_CollectsThePackageThatCarriesCountsOnly()
    {
        // Checked from the argument vector, not the parameter other tests hand over already decided: a flag that read as set whatever was typed would put blocked domains into every package.
        var report = await ReportAsync("diag");

        Assert.Contains("counts only", report, StringComparison.Ordinal);
        Assert.DoesNotContain(Domain, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diag_WithTheFlag_CollectsThePackageThatCarriesTheRulesThemselves()
    {
        var report = await ReportAsync("diag", "--verbose");

        Assert.Contains("verbose:", report, StringComparison.Ordinal);
        Assert.Contains(Domain, report, StringComparison.Ordinal);
    }

    private async Task<string> ReportAsync(params string[] args)
    {
        var exit = await CliProgram.DiagAsync(args, Elevated, Machine, _output, _error, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(1, _machineReads);

        using var archive = ZipFile.OpenRead(_output.ToString().Trim());
        var entry = archive.GetEntry("report.txt");

        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open());

        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Diag_OnAMachineTheToolCannotPlaceItselfOnSaysSoAndDoesNotFallOver()
    {
        // Environment.ProcessPath is null on a host that cannot say where the process came from, and the setup commands derive everything from it.
        // One caller is a scheduled boot task, where an unhandled exception is only a stack trace in the scheduler history.
        var exit = await CliProgram.DiagAsync(
            ["diag"],
            Elevated,
            () => throw new InvalidOperationException("Chronos could not tell where it is running from."),
            _output,
            _error,
            CancellationToken.None);

        Assert.Equal(4, exit);
        Assert.Contains("cannot tell what machine it is on", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _output.ToString());
    }

    [Fact]
    public void ThisMachine_WhenItCanBeDescribed_IsHandedBackAsItIs()
    {
        var machine = Machine();

        Assert.Same(machine, CliProgram.ThisMachine(() => machine, _error));
        Assert.Equal(string.Empty, _error.ToString());
    }

    [Fact]
    public async Task Clean_WithoutAdministratorRights_RefusesBeforeItChangesAnything()
    {
        // clean needs administrator like the other four commands. Unelevated it read the hosts file, printed a success line, then failed on the filter engine with 4:
        // half success and half failure, with a code the README gives to something else.
        var cleaned = false;

        var exit = await CliProgram.CleanAsync(
            () => false,
            () =>
            {
                cleaned = true;

                return Task.FromResult(0);
            },
            _error);

        Assert.Equal(2, exit);
        Assert.False(cleaned);
        Assert.Contains("'clean'", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("administrator rights", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_WithAdministratorRights_RunsAndAnswersWithWhatTheCleanupSaid()
    {
        var exit = await CliProgram.CleanAsync(Elevated, () => Task.FromResult(4), _error);

        Assert.Equal(4, exit);
        Assert.Equal(string.Empty, _error.ToString());
    }

    private static bool Elevated() => true;

    /// <summary>The machine, counted rather than reached: everything that would change it raises; only the session the service reports has anything to say.</summary>
    private SetupContext Machine()
    {
        _machineReads++;

        return new SetupContext(
            new FakeServiceRegistration(),
            _ => Task.FromResult(IpcResponse.Ok(new StatusPayload(
                State: "Idle",
                Now: DateTimeOffset.UnixEpoch,
                StartedAt: null,
                EndsAt: null,
                UnlockEffectiveAt: null,
                CoolDownMinutes: 15,
                Sites: [new SiteRuleMessage(Domain, IncludeSubdomains: true)],
                Apps: [],
                Layers: []))),
            new FakeTaskRegistration(),
            new FakeSystemEventLog(),
            new FakeAutostartEntry(),
            _paths,
            ServiceDefinition.Chronos(@"C:\Program Files\Chronos\Chronos.Service.exe"),
            @"C:\Program Files\Chronos\chronos.exe",
            (_, _) => throw new InvalidOperationException("Collecting diagnostics takes nothing off this machine."),
            () => throw new InvalidOperationException("Collecting diagnostics does not clear the session state."),
            _ => throw new InvalidOperationException("Collecting diagnostics does not create the data directory."),
            () => throw new InvalidOperationException("No filter engine is opened here."),
            () => null,
            _ => PortUse.Unknown,
            () => [],
            _ => new LoopbackPort(Free: true, Holder: null),
            () => new DnsBackupSources(File: null, Registry: null),
            (_, _) => throw new InvalidOperationException("Nothing here restores DNS."),
            () => throw new InvalidOperationException("Nothing here removes the product key."));
    }
}
