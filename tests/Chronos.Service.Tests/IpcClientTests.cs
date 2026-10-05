using Chronos.Core.Enforcement;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Ipc;
using Chronos.Service.Configuration;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Rules;
using Chronos.Service.Sessions;
using Chronos.Service.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class IpcClientTests : IDisposable
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 23, 9, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "chronos-test-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();

    private LayerStatusRegistry _layers = null!;

    private IpcServer BuildServer()
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var config = new ConfigStore(paths, NullLogger<ConfigStore>.Instance);
        config.Save(new ChronosConfig { Sites = [new SiteRuleDto("example.com", true)] });

        var clock = new ServiceTestClock(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var engine = new SessionEngine(clock, new WindowsProtectedAppPolicy());
        var layers = new LayerStatusRegistry(clock);
        _layers = layers;

        var gate = new SessionGate();

        var runner = new ReconcileRunner(
            engine,
            new ReconcileCoordinator([]),
            layers,
            new StateStore(paths, NullLogger<StateStore>.Instance),
            gate,
            new EventBus(),
            TestStatus.Blank,
            NullLogger<ReconcileRunner>.Instance);

        var dispatcher = new CommandDispatcher(
            engine,
            config,
            new ReconcileScheduler(runner, NullLogger<ReconcileScheduler>.Instance),
            layers,
            gate,
            clock,
            new WindowsProtectedAppPolicy(),
            new EventBus(),
            NullLogger<CommandDispatcher>.Instance);

        return IpcServer.ForTests(dispatcher, new EventBus(), NullLogger<IpcServer>.Instance, _pipeName);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RoundTripsAStatusRequest()
    {
        var running = BuildServer().RunAsync(_cts.Token);
        var client = new IpcClient(_pipeName);

        var response = await client.SendAsync(new IpcRequest { Command = "GetStatus" }, _cts.Token);

        Assert.True(response.Accepted);
        Assert.Equal(nameof(SessionState.Idle), response.Status!.State);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RoundTripsTheLayerAvailabilityOfTheLastPass()
    {
        // Round-trips the new field through JSON; a record that does not survive looks like a service that reported nothing.
        var running = BuildServer().RunAsync(_cts.Token);
        _layers.Record(
        [
            ReconcileResult.Unchanged("hosts"),
            ReconcileResult.Skipped("wfp", "wfp.engine-unavailable"),
        ]);

        var response = await new IpcClient(_pipeName).SendAsync(
            new IpcRequest { Command = "GetStatus" }, _cts.Token);

        var layers = response.Status!.Layers;
        Assert.Equal(["hosts", "wfp"], layers.Select(layer => layer.Name));
        Assert.True(layers[0].IsAvailable);
        Assert.False(layers[1].IsAvailable);
        Assert.Equal("wfp.engine-unavailable", layers[1].ReasonCode);
        Assert.NotNull(layers[1].ObservedAt);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RoundTripsAConfigurationChange()
    {
        // SettingsMessage and ConfigPayload cross JSON only here.
        var running = BuildServer().RunAsync(_cts.Token);
        var client = new IpcClient(_pipeName);

        var added = await client.SendAsync(
            new IpcRequest { Command = "AddSiteRule", Site = new SiteRuleMessage("reddit.com", true) },
            _cts.Token);

        Assert.True(added.Accepted);
        Assert.Equal(
            ["example.com", "reddit.com"],
            added.Config!.Sites.Select(site => site.Domain));

        var updated = await client.SendAsync(
            new IpcRequest { Command = "UpdateSettings", Settings = new SettingsMessage(CoolDownMinutes: 42) },
            _cts.Token);

        Assert.True(updated.Accepted);
        Assert.Equal(42, updated.Config!.CoolDownMinutes);
        Assert.Equal("system", updated.Config.Language);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public void TheStatusCommandPrintsALinePerLayer()
    {
        var output = new StringWriter();

        Cli.Program.Print(
            IpcResponse.Ok(new StatusPayload(
                nameof(SessionState.Active),
                Moment,
                Moment,
                Moment.AddHours(1),
                null,
                5,
                [],
                [],
                [
                    new LayerStatus("hosts", true, null, nameof(ReconcileOutcome.Unchanged), Moment),
                    new LayerStatus("wfp", false, "wfp.engine-unavailable", nameof(ReconcileOutcome.Skipped), Moment),
                ])),
            output,
            TextWriter.Null);

        var lines = output.ToString().Split(Environment.NewLine);
        Assert.Contains(lines, line => line.Contains("hosts", StringComparison.Ordinal) && line.Contains("available", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line.Contains("wfp", StringComparison.Ordinal)
                && line.Contains("unavailable", StringComparison.Ordinal)
                && line.Contains("wfp.engine-unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStatusCommandSaysSoWhenNoPassHasReportedOnTheLayers()
    {
        // Printing nothing would read as "there are no layers".
        var output = new StringWriter();

        Cli.Program.Print(
            IpcResponse.Ok(new StatusPayload(nameof(SessionState.Idle), Moment, null, null, null, 5, [], [], [])),
            output,
            TextWriter.Null);

        Assert.Contains("Layers", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStatusVerbAsksForSomethingTheServiceAnswersWithAConfiguration()
    {
        // What the verb actually sends; a name the dispatcher does not know is a run-time refusal.
        var running = BuildServer().RunAsync(_cts.Token);

        var response = await new IpcClient(_pipeName).SendAsync(
            new IpcRequest { Command = Cli.Program.MapCommand("status")! }, _cts.Token);

        Assert.True(response.Accepted);
        Assert.NotNull(response.Status);
        Assert.NotNull(response.Config);
        Assert.Equal(["example.com"], response.Config!.Sites.Select(site => site.Domain));

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public void TheStatusCommandReportsTheConfiguredRulesWhenThereIsNoSession()
    {
        // An idle machine has no session snapshot, so its counts are zero however many rules are configured.
        var output = new StringWriter();

        Cli.Program.Print(
            IpcResponse.Ok(
                new StatusPayload(nameof(SessionState.Idle), Moment, null, null, null, 5, [], [], []),
                Configured),
            output,
            TextWriter.Null);

        Assert.Contains("2 sites, 1 apps", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusCommandTellsTheSessionsRulesApartFromTheConfiguredOnes()
    {
        var output = new StringWriter();

        Cli.Program.Print(
            IpcResponse.Ok(
                new StatusPayload(
                    nameof(SessionState.Active),
                    Moment,
                    Moment,
                    Moment.AddHours(1),
                    null,
                    5,
                    [new SiteRuleMessage("reddit.com", true)],
                    [],
                    []),
                Configured),
            output,
            TextWriter.Null);

        var lines = output.ToString().Split(Environment.NewLine);

        // A rule added during a session is in the configuration but not in what the session locked in.
        Assert.Contains(lines, line => line.StartsWith("Locked:", StringComparison.Ordinal) && line.Contains("1 sites", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Config:", StringComparison.Ordinal) && line.Contains("2 sites", StringComparison.Ordinal));
    }

    private static ConfigPayload Configured => new(
        [new SiteRuleMessage("reddit.com", true), new SiteRuleMessage("news.example", false)],
        [new AppRuleMessage(nameof(AppMatchKind.FileName), "steam.exe")],
        CoolDownMinutes: 5,
        DefaultSessionMinutes: 60,
        Language: "system",
        VerboseLogging: false,
        WfpEnabled: true);

    [Fact]
    public async Task ReportsAnUnreachableService()
    {
        var client = new IpcClient("chronos-nobody-" + Guid.NewGuid().ToString("N"));

        var thrown = await Record.ExceptionAsync(
            () => client.SendAsync(new IpcRequest { Command = "GetStatus" }, _cts.Token));

        Assert.NotNull(thrown);
        Assert.True(
            thrown is IOException or TimeoutException or UnauthorizedAccessException,
            $"The CLI only maps IOException, TimeoutException and UnauthorizedAccessException to exit code 3, "
            + $"but the client threw {thrown!.GetType().Name}.");
    }

    private static async Task<(int ExitCode, string Error)> RunCliAsync(params string[] args)
    {
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);

        try
        {
            var exitCode = await Chronos.Cli.Program.Main(args);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public async Task RejectsMinutesWithoutAValue()
    {
        var (exitCode, error) = await RunCliAsync("start", "--minutes");

        Assert.Equal(2, exitCode);
        Assert.Contains("--minutes", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsANonNumericMinutesValue()
    {
        var (exitCode, error) = await RunCliAsync("start", "--minutes", "abc");

        Assert.Equal(2, exitCode);
        Assert.Contains("abc", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAnUnknownCommand()
    {
        var (exitCode, error) = await RunCliAsync("frobnicate");

        Assert.Equal(2, exitCode);
        Assert.Contains("frobnicate", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsNoArgumentsAtAll()
    {
        var (exitCode, error) = await RunCliAsync();

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamesTheWatchCommandInTheUsageLine()
    {
        var (_, error) = await RunCliAsync();

        Assert.Contains("watch", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsArgumentsTheWatchCommandDoesNotTake()
    {
        // The only test of the verb itself: an unrouted "watch" would print "Unknown command" with the same exit code.
        // Other checks bypass Main, which can only use the real pipe.
        var (exitCode, error) = await RunCliAsync("watch", "--minutes", "5");

        Assert.Equal(2, exitCode);
        Assert.Contains("watch takes no arguments", error, StringComparison.Ordinal);
    }
}
