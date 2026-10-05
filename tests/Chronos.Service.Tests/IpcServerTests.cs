using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Chronos.Core.Enforcement;
using Chronos.Core.Sessions;
using Chronos.Ipc;
using Chronos.Service.Configuration;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.Rules;
using Chronos.Service.Sessions;
using Chronos.Service.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

public sealed class IpcServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "chronos-test-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();
    private readonly EventBus _bus = new();

    /// <summary>
    /// A server on this test's own pipe, through the seam. The service ACL grants Interactive ReadWrite without CreateNewInstance,
    /// so an unelevated test could not create a second listening instance; the production ACL is covered by <see cref="TheServiceHostsItsPipeWithThreeRulesAndNoOthers"/>.
    /// </summary>
    private IpcServer BuildServer(
        TimeSpan? connectionTimeout = null,
        ILogger<CommandDispatcher>? dispatcherLogger = null,
        ILogger<IpcServer>? serverLogger = null) =>
        IpcServer.ForTests(
            BuildDispatcher(dispatcherLogger),
            _bus,
            serverLogger ?? NullLogger<IpcServer>.Instance,
            _pipeName,
            connectionTimeout);

    private CommandDispatcher BuildDispatcher(ILogger<CommandDispatcher>? dispatcherLogger = null)
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
            new EventBus(),
            TestStatus.Blank,
            NullLogger<ReconcileRunner>.Instance);

        return new CommandDispatcher(
            engine,
            config,
            new ReconcileScheduler(runner, NullLogger<ReconcileScheduler>.Instance),
            layers,
            gate,
            clock,
            new WindowsProtectedAppPolicy(),
            _bus,
            dispatcherLogger ?? NullLogger<CommandDispatcher>.Instance);
    }

    /// <summary>
    /// The pipe ACL written down as three rules and nothing else, on a server built the way Program builds it:
    /// LocalSystem and Administrators FullControl, an interactive logon session ReadWrite, nobody else.
    /// ReadWrite must not include CreateNewInstance, or an ordinary user could create their own instance of the pipe and be handed the service's clients.
    /// </summary>
    [Fact]
    public void TheServiceHostsItsPipeWithThreeRulesAndNoOthers()
    {
        var server = new IpcServer(BuildDispatcher(), _bus, NullLogger<IpcServer>.Instance, _pipeName);

        Assert.Equal(
            [
                (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                    PipeAccessRights.FullControl, AccessControlType.Allow),
                (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                    PipeAccessRights.FullControl, AccessControlType.Allow),
                // Synchronize is not something the service asks for: PipeAccessRule adds it to every
                // Allow rule, and FullControl contains it already. What matters is what is absent.
                (new SecurityIdentifier(WellKnownSidType.InteractiveSid, null).Value,
                    PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow),
            ],
            Rules(server.PipeAcl()));
    }

    /// <summary>The seam grants nobody but the account already running the test, so a test pipe hands out no right that account lacked; nothing here belongs in production.</summary>
    [Fact]
    public void ATestServerNamesNobodyButTheAccountRunningIt()
    {
        using var identity = WindowsIdentity.GetCurrent();

        Assert.Equal(
            [(identity.User!.Value, PipeAccessRights.FullControl, AccessControlType.Allow)],
            Rules(BuildServer().PipeAcl()));
    }

    /// <summary>The seam cannot answer on the name the interface and the CLI look for, however it is called.</summary>
    [Theory]
    [InlineData(IpcProtocol.DefaultPipeName)]
    [InlineData("CHRONOS")]
    public void ATestServerRefusesTheServicesOwnPipeName(string name)
    {
        var refused = Assert.Throws<ArgumentException>(
            () => IpcServer.ForTests(BuildDispatcher(), _bus, NullLogger<IpcServer>.Instance, name));

        Assert.Equal("pipeName", refused.ParamName);
    }

    /// <summary>Every rule on the list, ordered by identity rather than Windows' canonical order, which a test about who is on the list should not depend on.</summary>
    private static (string Sid, PipeAccessRights Rights, AccessControlType Type)[] Rules(PipeSecurity acl) =>
    [
        .. acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Select(rule => (Sid: rule.IdentityReference.Value, rule.PipeAccessRights, rule.AccessControlType))
            .OrderBy(rule => rule.Sid, StringComparer.Ordinal),
    ];

    private Task<IpcResponse> SendAsync(IpcRequest request) => SendAsync(request, _cts.Token);

    private async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(TimeSpan.FromSeconds(5), ct);

        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json));
        var line = await reader.ReadLineAsync(ct);

        return JsonSerializer.Deserialize<IpcResponse>(line!, Json)!;
    }

    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(5), _cts.Token);
        }
        catch
        {
            await client.DisposeAsync();

            throw;
        }

        return client;
    }

    /// <summary>Stops a connection inside the dispatcher until the test releases it. CommandDispatcher logs one entry per accepted command under the session gate, so its logger is where a connection can be held without a fake.</summary>
    private sealed class HeldDispatch : ILogger<CommandDispatcher>, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _released = new();

        public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);

        public void Release() => _released.Set();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entered.Set();
            _released.Wait();
        }

        public void Dispose()
        {
            // A test that failed before releasing must not leave a connection stuck forever.
            _released.Set();
            _entered.Dispose();
            _released.Dispose();
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
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
    public async Task AnswersAStatusRequest()
    {
        var server = BuildServer();
        var running = server.RunAsync(_cts.Token);

        var response = await SendAsync(new IpcRequest { Command = "GetStatus" });

        Assert.True(response.Accepted);
        Assert.Equal(nameof(SessionState.Idle), response.Status!.State);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task ServesSeveralRequestsInSequence()
    {
        var server = BuildServer();
        var running = server.RunAsync(_cts.Token);

        var start = await SendAsync(new IpcRequest { Command = "StartSession", DurationMinutes = 60 });
        var status = await SendAsync(new IpcRequest { Command = "GetStatus" });

        Assert.True(start.Accepted);
        Assert.Equal(nameof(SessionState.Active), status.Status!.State);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Theory]
    [InlineData("{ not json", IpcCodes.RequestInvalidJson)]
    [InlineData("null", IpcCodes.RequestEmpty)]
    public async Task ReportsAnErrorForMalformedInputWithoutDying(string sent, string code)
    {
        var server = BuildServer();
        var running = server.RunAsync(_cts.Token);

        await using (var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(5), _cts.Token);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);

            await writer.WriteLineAsync(sent);
            var line = await reader.ReadLineAsync(_cts.Token);

            Assert.NotNull(line);
            var answer = JsonSerializer.Deserialize<IpcResponse>(line!, IpcJson.Options)!;
            Assert.False(answer.Accepted);
            Assert.Equal(code, answer.Error);
        }

        var afterwards = await SendAsync(new IpcRequest { Command = "GetStatus" });
        Assert.True(afterwards.Accepted);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task AClientThatNeverSendsAnythingDoesNotBlockOtherClients()
    {
        var server = BuildServer(connectionTimeout: TimeSpan.FromMilliseconds(200));
        var running = server.RunAsync(_cts.Token);

        await using var silent = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await silent.ConnectAsync(TimeSpan.FromSeconds(5), _cts.Token);

        // The silent client is deliberately still connected here.
        var afterwards = await SendAsync(new IpcRequest { Command = "GetStatus" });

        Assert.True(afterwards.Accepted);

        // And the deadline still reclaims it: the server closes its end, which the client sees as
        // the end of the stream. Without this the test would pass on a server that never gives up.
        using var silentReader = new StreamReader(silent, leaveOpen: true);
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        patience.CancelAfter(TimeSpan.FromSeconds(10));

        Assert.Null(await silentReader.ReadLineAsync(patience.Token));

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task ServesASecondClientWhileTheFirstConnectionIsStillOpen()
    {
        var server = BuildServer();
        var running = server.RunAsync(_cts.Token);

        // A client that connects and then says nothing at all - exactly what a subscriber looks
        // like from the server's side between two events.
        await using var idle = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await idle.ConnectAsync(TimeSpan.FromSeconds(5), _cts.Token);

        var answer = await SendAsync(new IpcRequest { Command = "GetStatus" });

        Assert.True(answer.Accepted);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RunAsync_DoesNotReturnUntilOpenConnectionsAreDone()
    {
        using var held = new HeldDispatch();
        var server = BuildServer(dispatcherLogger: held);
        var running = server.RunAsync(_cts.Token);

        // Its own token: this client has to survive the shutdown it is about to trigger.
        using var client = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = SendAsync(new IpcRequest { Command = "StartSession", DurationMinutes = 60 }, client.Token);

        Assert.True(held.WaitUntilEntered(TimeSpan.FromSeconds(10)));

        await _cts.CancelAsync();

        var settled = await Task.WhenAny(running, Task.Delay(TimeSpan.FromMilliseconds(500), client.Token));
        Assert.NotSame(running, settled);

        held.Release();

        // The connection that was in flight when shutdown began is still answered.
        var answer = await request;
        Assert.True(answer.Accepted);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RunAsync_TakesNoMoreConnectionsThanItsLimitAndTakesOneAgainWhenASlotFrees()
    {
        // Long enough that nothing is dropped for silence while the limit is being filled.
        var server = BuildServer(connectionTimeout: TimeSpan.FromMinutes(5));
        var running = server.RunAsync(_cts.Token);

        var held = new List<NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < 16; i++)
            {
                held.Add(await ConnectAsync());
            }

            await using (var beyond = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                // The server stops listening once its slots are taken, so the seventeenth client
                // finds no instance to connect to rather than being accepted and left unserved.
                await Assert.ThrowsAsync<TimeoutException>(
                    () => beyond.ConnectAsync(TimeSpan.FromSeconds(1), _cts.Token));
            }

            await held[0].DisposeAsync();
            held.RemoveAt(0);

            var answer = await SendAsync(new IpcRequest { Command = "GetStatus" });
            Assert.True(answer.Accepted);
        }
        finally
        {
            foreach (var client in held)
            {
                await client.DisposeAsync();
            }
        }

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RunAsync_SaysSoWhenEverySlotIsTakenAndSaysItOnlyOnce()
    {
        var reported = new CountingLogger();
        var server = BuildServer(connectionTimeout: TimeSpan.FromMinutes(5), serverLogger: reported);
        var running = server.RunAsync(_cts.Token);

        var held = new List<NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < 16; i++)
            {
                held.Add(await ConnectAsync());
            }

            // Without this the cap is invisible: the accept loop is parked and never learns that anyone tried, so the client's TimeoutException is the only trace of the refusal.
            await WaitUntilAsync(() => reported.Failures > 0);
            Assert.Contains("connection slots", reported.Reported, StringComparison.Ordinal);

            // Churn under a cap that stays full: one client leaves, the next takes its place, and
            // the loop parks again. A line for each of them would be the flood the suppression
            // exists to stop, so the count must not move.
            await held[0].DisposeAsync();
            held.RemoveAt(0);
            held.Add(await ConnectAsync());

            await using (var beyond = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                // Waiting for the loop to be parked again, and asserting the refusal while at it.
                await Assert.ThrowsAsync<TimeoutException>(
                    () => beyond.ConnectAsync(TimeSpan.FromSeconds(1), _cts.Token));
            }

            Assert.Equal(1, reported.Failures);
        }
        finally
        {
            foreach (var client in held)
            {
                await client.DisposeAsync();
            }
        }

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RunAsync_KeepsServingAConnectionThatOutlivesTheDeadline()
    {
        using var held = new HeldDispatch();
        var server = BuildServer(connectionTimeout: TimeSpan.FromMilliseconds(200), dispatcherLogger: held);
        var running = server.RunAsync(_cts.Token);

        var request = SendAsync(new IpcRequest { Command = "StartSession", DurationMinutes = 60 });

        Assert.True(held.WaitUntilEntered(TimeSpan.FromSeconds(10)));

        // Four deadlines' worth of work after the request arrived. The deadline drops a client that asks for nothing; a subscriber asks once and listens for hours, so the clock must stop at the request.
        await Task.Delay(TimeSpan.FromMilliseconds(800), _cts.Token);
        held.Release();

        var answer = await request;
        Assert.True(answer.Accepted);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RunAsync_WaitsBeforeRetryingAnAcceptThatKeepsFailing()
    {
        // Another process owns the name and allows one instance only, so every attempt to create a
        // listening instance fails the same way for as long as this stream lives.
        using var squatter = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var attempts = new CountingLogger();
        var server = BuildServer(serverLogger: attempts);
        var running = server.RunAsync(_cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(1200), CancellationToken.None);
        var observed = attempts.Failures;

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        // A second between attempts, so a bit over a second of squatting is one or two of them. With no delay it spun millions of times on one core.
        Assert.InRange(observed, 1, 5);
    }

    [Fact]
    public async Task RunAsync_StopsWithinItsDrainBoundWhenAClientWillNotReadItsAnswer()
    {
        var server = BuildServer(connectionTimeout: TimeSpan.FromMinutes(5));
        var running = server.RunAsync(_cts.Token);

        // An unknown command is quoted back inside the error text, so the client chooses how much
        // the server has to write - and then reads none of it. The pipe buffer fills and the write
        // blocks for as long as this client stays connected.
        await using var deaf = await ConnectAsync();
        using var writer = new StreamWriter(deaf, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(new IpcRequest { Command = new string('x', 4 * 1024 * 1024) }, Json));

        // Long enough that the answer is under way rather than still being composed.
        await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);

        var clock = Stopwatch.StartNew();
        await _cts.CancelAsync();

        var settled = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None));
        clock.Stop();

        // Sixteen such clients held an unbounded version open for the whole twenty seconds measured. A stop must not wait on a client's goodwill.
        Assert.Same(running, settled);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the stop took {clock.Elapsed}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Subscribe_AnswersWithTheStatusAndThenDeliversAnEventPublishedAfterwards()
    {
        // Short on purpose: the deadline must not reach a connection that has already asked.
        var server = BuildServer(connectionTimeout: TimeSpan.FromMilliseconds(300));
        var running = server.RunAsync(_cts.Token);

        await using var subscriber = await ConnectAsync();
        using var writer = new StreamWriter(subscriber, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(subscriber, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest { Command = "Subscribe" }, Json));

        // The acknowledgement carries the status, so there is no window between subscribing and
        // knowing the state in which an event could describe a change from something unknown.
        var acknowledgement = JsonSerializer.Deserialize<IpcResponse>((await reader.ReadLineAsync(_cts.Token))!, Json)!;
        Assert.True(acknowledgement.Accepted);
        Assert.Equal(nameof(SessionState.Idle), acknowledgement.Status!.State);

        await WaitUntilAsync(() => _bus.SubscriberCount == 1);
        _bus.Publish(new IpcEvent(IpcEventKind.AppBlocked, DateTimeOffset.UnixEpoch, null, "game.exe", null));

        var pushed = JsonSerializer.Deserialize<IpcEvent>((await reader.ReadLineAsync(_cts.Token))!, Json)!;
        Assert.Equal(IpcEventKind.AppBlocked, pushed.Kind);
        Assert.Equal("game.exe", pushed.AppName);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    /// <summary>The subscription exists before the acknowledgement is composed, so no event falls between the status the client gets and the stream after it. A StartSession held inside the gate keeps Subscribe from answering; the event published meanwhile must still arrive.</summary>
    [Fact]
    public async Task Subscribe_DeliversAnEventPublishedBeforeTheAcknowledgementWasWritten()
    {
        using var held = new HeldDispatch();
        var server = BuildServer(dispatcherLogger: held);
        var running = server.RunAsync(_cts.Token);

        using var client = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = SendAsync(new IpcRequest { Command = "StartSession", DurationMinutes = 60 }, client.Token);
        Assert.True(held.WaitUntilEntered(TimeSpan.FromSeconds(10)));

        await using var subscriber = await ConnectAsync();
        using var writer = new StreamWriter(subscriber, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(subscriber, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest { Command = "Subscribe" }, Json));

        // Subscribed while its answer is still waiting on the gate.
        await WaitUntilAsync(() => _bus.SubscriberCount == 1);
        _bus.Publish(new IpcEvent(IpcEventKind.AppBlocked, DateTimeOffset.UnixEpoch, null, "game.exe", null));

        held.Release();
        Assert.True((await start).Accepted);

        var acknowledgement = JsonSerializer.Deserialize<IpcResponse>((await reader.ReadLineAsync(client.Token))!, Json)!;
        Assert.True(acknowledgement.Accepted);

        var pushed = JsonSerializer.Deserialize<IpcEvent>((await reader.ReadLineAsync(client.Token))!, Json)!;
        Assert.Equal(IpcEventKind.AppBlocked, pushed.Kind);
        Assert.Equal("game.exe", pushed.AppName);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Subscribe_DeliversTheEventACommandOnAnotherConnectionProduced()
    {
        var server = BuildServer();
        var running = server.RunAsync(_cts.Token);

        await using var subscriber = await ConnectAsync();
        using var writer = new StreamWriter(subscriber, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(subscriber, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest { Command = "Subscribe" }, Json));
        Assert.NotNull(await reader.ReadLineAsync(_cts.Token));
        await WaitUntilAsync(() => _bus.SubscriberCount == 1);

        // The whole point of the stream: nobody asked this connection for anything.
        var start = await SendAsync(new IpcRequest { Command = "StartSession", DurationMinutes = 60 });
        Assert.True(start.Accepted);

        var pushed = JsonSerializer.Deserialize<IpcEvent>((await reader.ReadLineAsync(_cts.Token))!, Json)!;
        Assert.Equal(IpcEventKind.StatusChanged, pushed.Kind);
        Assert.Equal(nameof(SessionState.Active), pushed.Status!.State);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Subscribe_LetsGoOfTheSubscriptionWhenTheClientDisappears()
    {
        var server = BuildServer(connectionTimeout: TimeSpan.FromMinutes(5));
        var running = server.RunAsync(_cts.Token);

        var subscriber = await ConnectAsync();
        using (var writer = new StreamWriter(subscriber, leaveOpen: true) { AutoFlush = true })
        using (var reader = new StreamReader(subscriber, leaveOpen: true))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest { Command = "Subscribe" }, Json));
            Assert.NotNull(await reader.ReadLineAsync(_cts.Token));
        }

        await WaitUntilAsync(() => _bus.SubscriberCount == 1);
        await subscriber.DisposeAsync();

        // Without a publish of any kind. An idle machine can go hours between events, and a
        // subscriber whose departure is only noticed by the next write would hold one of the
        // sixteen connection slots for all of them.
        await WaitUntilAsync(() => _bus.SubscriberCount == 0);

        var afterwards = await SendAsync(new IpcRequest { Command = "GetStatus" });
        Assert.True(afterwards.Accepted);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Publish_AfterASubscriberDisconnects_DoesNotFaultTheServer()
    {
        var failures = new CountingLogger();
        var server = BuildServer(connectionTimeout: TimeSpan.FromMinutes(5), serverLogger: failures);
        var running = server.RunAsync(_cts.Token);

        var subscriber = await ConnectAsync();
        using (var writer = new StreamWriter(subscriber, leaveOpen: true) { AutoFlush = true })
        using (var reader = new StreamReader(subscriber, leaveOpen: true))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest { Command = "Subscribe" }, Json));
            Assert.NotNull(await reader.ReadLineAsync(_cts.Token));
        }

        await WaitUntilAsync(() => _bus.SubscriberCount == 1);

        // A client that went away is not an event the service has an opinion about.
        for (var i = 0; i < 200; i++)
        {
            _bus.Publish(new IpcEvent(IpcEventKind.AppBlocked, DateTimeOffset.UnixEpoch, null, "game.exe", null));
        }

        // The pipe has no buffer of its own and this client reads nothing after its acknowledgement, so the server is blocked in a write by now.
        // Killing it here matters: a client that vanishes before the first write leaves through cancellation, which was never the failing case.
        await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await subscriber.DisposeAsync();

        var afterwards = await SendAsync(new IpcRequest { Command = "GetStatus" });
        Assert.True(afterwards.Accepted);

        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        // After the stop: the connection task is only certainly finished once RunAsync has drained it, and asserting earlier passes by outrunning the server.
        // A killed watcher leaves nothing above Debug behind, and one Debug line saying it went away.
        Assert.True(failures.Failures == 0, $"the server reported {failures.Reported}");
    }

    [Fact]
    public async Task RunAsync_StopsAtOnceWithASubscriberStillAttached()
    {
        var server = BuildServer(connectionTimeout: TimeSpan.FromMinutes(5));
        var running = server.RunAsync(_cts.Token);

        await using var subscriber = await ConnectAsync();
        using var writer = new StreamWriter(subscriber, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(subscriber, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest { Command = "Subscribe" }, Json));
        Assert.NotNull(await reader.ReadLineAsync(_cts.Token));
        await WaitUntilAsync(() => _bus.SubscriberCount == 1);

        var clock = Stopwatch.StartNew();
        await _cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        clock.Stop();

        // The drain bound is five seconds. A subscriber sits idle by design, so if it does not
        // notice cancellation the service pays that bound on every single stop.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the stop took {clock.Elapsed}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var patience = Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(patience.Elapsed < TimeSpan.FromSeconds(10), "the condition never came true");
            await Task.Delay(20, CancellationToken.None);
        }
    }

    /// <summary>
    /// Counts entries above <see cref="LogLevel.Debug"/> instead of keeping them all: <see cref="CapturingLogger{T}"/> stores every entry and the defect this catches produces millions.
    /// A few are kept so a failure can say what was written. Counted by level, not by exception: a closed client is reported at Debug with its IOException.
    /// </summary>
    private sealed class CountingLogger : ILogger<IpcServer>
    {
        private const int Kept = 20;

        private readonly Lock _sync = new();
        private readonly List<string> _reported = [];
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        /// <summary>The first few of them, so a failure can say what it saw and not only how much.</summary>
        public string Reported
        {
            get
            {
                lock (_sync)
                {
                    return _reported.Count == 0 ? "nothing" : string.Join("; ", _reported);
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (logLevel < LogLevel.Warning)
            {
                return;
            }

            Interlocked.Increment(ref _failures);

            lock (_sync)
            {
                if (_reported.Count < Kept)
                {
                    _reported.Add($"{logLevel}: {formatter(state, exception)} [{exception?.GetType().Name ?? "no exception"}]");
                }
            }
        }
    }
}
