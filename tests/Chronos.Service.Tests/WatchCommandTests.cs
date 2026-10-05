using System.Diagnostics;
using System.Globalization;
using System.Text;
using Chronos.Core.Enforcement;
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

/// <summary>The command that follows the event stream, and the client method underneath it. Everything is bounded: a subscription never ends normally, so an unbounded wait would hang the suite.</summary>
public sealed class WatchCommandTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Moment = new(2026, 8, 24, 9, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "chronos-test-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();
    private readonly EventBus _bus = new();

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
    public void PrintEvent_LeadsWithTheKindAndTimesTheEventInLocalTime()
    {
        // An offset of its own, so a machine anywhere but UTC+5 proves the line was converted
        // rather than the wire value printed straight back.
        var at = new DateTimeOffset(2026, 8, 24, 9, 30, 0, TimeSpan.FromHours(5));
        var output = new StringWriter();

        Cli.Program.PrintEvent(
            new IpcEvent(IpcEventKind.StatusChanged, at, Status("Active", at.AddHours(1)), null, null), output);

        var line = output.ToString().TrimEnd();
        Assert.StartsWith(IpcEventKind.StatusChanged, line, StringComparison.Ordinal);
        Assert.Contains(
            TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            line,
            StringComparison.Ordinal);
        Assert.Contains("state=Active", line, StringComparison.Ordinal);
        Assert.Contains("ends=", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintEvent_NamesTheApplicationThatWasBlocked()
    {
        // Printed: the name comes from the user's list and is a file name, not a path.
        var output = new StringWriter();

        Cli.Program.PrintEvent(
            new IpcEvent(IpcEventKind.AppBlocked, Moment, Status("Active", Moment.AddHours(1)), "game.exe", null),
            output);

        var line = output.ToString().TrimEnd();
        Assert.StartsWith(IpcEventKind.AppBlocked, line, StringComparison.Ordinal);
        Assert.Contains("app=game.exe", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintEvent_KeepsTheBlockedDomainsAndApplicationPathsOutOfTheTerminal()
    {
        // The terminal follows the same privacy rule as the log: a full path is Debug material, and neither list is what an event-stream reader wants.
        var status = new StatusPayload(
            nameof(SessionState.Active),
            Moment,
            Moment,
            Moment.AddHours(1),
            null,
            5,
            [new SiteRuleMessage("reddit.com", true)],
            [new AppRuleMessage("FullPath", @"C:\Users\someone\Games\steam.exe")],
            []);
        var output = new StringWriter();

        Cli.Program.PrintEvent(new IpcEvent(IpcEventKind.StatusChanged, Moment, status, null, null), output);

        var printed = output.ToString();
        Assert.DoesNotContain("reddit.com", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("steam.exe", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintEvent_ReportsARefusedSiteWithoutNamingIt()
    {
        // The domain came from an attempt, not from the list; it stays out of the terminal as it stays out of logs above Debug.
        var output = new StringWriter();

        Cli.Program.PrintEvent(IpcEvent.SiteBlocked(Status("Active", Moment.AddHours(1)), "reddit.com"), output);

        var line = output.ToString().TrimEnd();
        Assert.StartsWith(IpcEventKind.SiteBlocked, line, StringComparison.Ordinal);
        Assert.DoesNotContain("reddit.com", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintEvent_SaysSoWhenAnEventCarriesNoStatus()
    {
        // Printing the kind and the time alone would read as a status that is somehow empty.
        var output = new StringWriter();

        Cli.Program.PrintEvent(new IpcEvent(IpcEventKind.AppBlocked, Moment, null, "game.exe", null), output);

        var line = output.ToString().TrimEnd();
        Assert.Contains("no status", line, StringComparison.Ordinal);
        Assert.Contains("app=game.exe", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_ReportsAnUnreachableServiceTheWayStatusDoes()
    {
        var error = new StringWriter();

        var exitCode = await Cli.Program
            .WatchAsync(new IpcClient("chronos-nobody-" + Guid.NewGuid().ToString("N")), TextWriter.Null, error, _cts.Token)
            .WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Contains("not reachable", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A refused subscription is worded from its code, and the exit code still says rejected.</summary>
    [Fact]
    public async Task Watch_PrintsARefusedSubscriptionInWords()
    {
        await using var server = new System.IO.Pipes.NamedPipeServerStream(
            _pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);

        var answering = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(_cts.Token);
            using var reader = new StreamReader(server, leaveOpen: true);
            await reader.ReadLineAsync(_cts.Token);
            await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(
                System.Text.Json.JsonSerializer.Serialize(IpcResponse.Fail(IpcCodes.RequestEmpty), IpcJson.Options));
        });

        var error = new StringWriter();
        var exitCode = await Cli.Program
            .WatchAsync(new IpcClient(_pipeName), TextWriter.Null, error, _cts.Token)
            .WaitAsync(Patience, CancellationToken.None);
        await answering.WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("Request is empty.", error.ToString().Trim());
    }

    [Fact]
    public async Task Watch_PrintsTheStatusItSubscribedWithAndThenEveryEvent()
    {
        var running = BuildServer().RunAsync(_cts.Token);
        var output = new SharedWriter();
        using var stopping = new CancellationTokenSource();

        var watching = Cli.Program.WatchAsync(new IpcClient(_pipeName), output, TextWriter.Null, stopping.Token);

        // The acknowledgement carries the status, so the first thing printed is the current state; no event then describes a change from something unannounced.
        await WaitUntilAsync(() => output.ToString().Contains("State:", StringComparison.Ordinal));
        await WaitUntilAsync(() => _bus.SubscriberCount == 1);

        _bus.Publish(new IpcEvent(IpcEventKind.AppBlocked, Moment, Status("Active", Moment.AddHours(1)), "game.exe", null));

        await WaitUntilAsync(() => output.ToString().Contains(IpcEventKind.AppBlocked, StringComparison.Ordinal));
        Assert.Contains("app=game.exe", output.ToString(), StringComparison.Ordinal);

        await stopping.CancelAsync();
        Assert.Equal(0, await watching.WaitAsync(Patience, CancellationToken.None));

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Watch_EndsQuietlyWhenItIsInterrupted()
    {
        // The way this command is stopped every single time it is used. A stack trace, or an exit
        // code that says something went wrong, would be wrong on every one of those occasions.
        var running = BuildServer().RunAsync(_cts.Token);
        var error = new SharedWriter();
        using var stopping = new CancellationTokenSource();

        var watching = Cli.Program.WatchAsync(new IpcClient(_pipeName), TextWriter.Null, error, stopping.Token);
        await WaitUntilAsync(() => _bus.SubscriberCount == 1);

        await stopping.CancelAsync();

        Assert.Equal(0, await watching.WaitAsync(Patience, CancellationToken.None));
        Assert.Equal(string.Empty, error.ToString());

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Watch_ReportsTheServiceStoppingWhileItWatches()
    {
        // Not an interruption: nobody asked this command to stop, so ending with success would
        // tell a script that the stream it was reading simply ran out of events.
        var running = BuildServer().RunAsync(_cts.Token);
        var error = new SharedWriter();

        var watching = Cli.Program.WatchAsync(
            new IpcClient(_pipeName), TextWriter.Null, error, CancellationToken.None);

        await WaitUntilAsync(() => _bus.SubscriberCount == 1);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(3, await watching.WaitAsync(Patience, CancellationToken.None));
        Assert.Contains("event stream", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeAsync_HandsBackTheAcknowledgementBeforeAnyEventIsRead()
    {
        // The shape matters more than the command: connecting can fail, and it has to fail here
        // rather than inside a loop the caller has already committed to.
        var running = BuildServer().RunAsync(_cts.Token);

        await using (var subscription = await new IpcClient(_pipeName).SubscribeAsync(_cts.Token)
            .WaitAsync(Patience, CancellationToken.None))
        {
            Assert.True(subscription.Acknowledgement.Accepted);
            Assert.Equal(nameof(SessionState.Idle), subscription.Acknowledgement.Status!.State);

            await WaitUntilAsync(() => _bus.SubscriberCount == 1);
            _bus.Publish(new IpcEvent(IpcEventKind.AppBlocked, Moment, null, "game.exe", null));

            using var reading = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            reading.CancelAfter(Patience);

            await foreach (var published in subscription.ReadEventsAsync(reading.Token))
            {
                Assert.Equal(IpcEventKind.AppBlocked, published.Kind);
                Assert.Equal("game.exe", published.AppName);

                break;
            }
        }

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    private static StatusPayload Status(string state, DateTimeOffset? endsAt) =>
        new(state, Moment, Moment, endsAt, null, 5, [], [], []);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var patience = Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(patience.Elapsed < Patience, "the condition never came true");
            await Task.Delay(20, CancellationToken.None);
        }
    }

    private IpcServer BuildServer()
    {
        var paths = new ChronosPaths(_root, Path.Combine(_root, "user"));
        paths.EnsureDataDirectoryExists();

        var config = new ConfigStore(paths, NullLogger<ConfigStore>.Instance);
        config.Save(new ChronosConfig { Sites = [new SiteRuleDto("example.com", true)] });

        var clock = new ServiceTestClock(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var engine = new SessionEngine(clock, new WindowsProtectedAppPolicy());
        var layers = new LayerStatusRegistry(clock);
        var gate = new SessionGate();

        var runner = new ReconcileRunner(
            engine,
            new ReconcileCoordinator([]),
            layers,
            new StateStore(paths, NullLogger<StateStore>.Instance),
            gate,
            _bus,
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
            _bus,
            NullLogger<CommandDispatcher>.Instance);

        return IpcServer.ForTests(dispatcher, _bus, NullLogger<IpcServer>.Instance, _pipeName);
    }

    /// <summary>A writer the command writes to on its own task while the test reads; StringWriter is not safe for that and the failure looks like a missing event.</summary>
    private sealed class SharedWriter : TextWriter
    {
        private readonly Lock _sync = new();
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_sync)
            {
                _text.Append(value);
            }
        }

        public override string ToString()
        {
            lock (_sync)
            {
                return _text.ToString();
            }
        }
    }
}
