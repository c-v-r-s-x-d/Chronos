using System.IO.Pipes;
using Chronos.App.Services;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>A status that parsed and lost a list on the way. The service here is a test-owned pipe writing the lines it is given.</summary>
public sealed class UnusableStatusLinkTests : IAsyncLifetime
{
    private const string Whole =
        """{"state":"Idle","now":"2026-08-24T09:30:00+00:00","coolDownMinutes":5,"sites":[],"apps":[],"layers":[]}""";

    private const string Later =
        """{"state":"Active","now":"2026-08-24T09:31:00+00:00","coolDownMinutes":5,"sites":[],"apps":[],"layers":[]}""";

    private const string NoLayers =
        """{"state":"Active","now":"2026-08-24T09:32:00+00:00","coolDownMinutes":5,"sites":[],"apps":[]}""";

    private readonly string _pipeName = "chronos-test-" + Guid.NewGuid().ToString("N");
    private readonly RecordedWaits _waits = new();
    private readonly CancellationTokenSource _stop = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _stop.Cancel();
        _stop.Dispose();

        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("""{"accepted":true,"status":{"state":"Idle","now":"2026-08-24T09:30:00+00:00"}}""")]
    [InlineData("""{"accepted":true,"status":{"state":"Idle","now":"2026-08-24T09:30:00+00:00","sites":[],"apps":[]}}""")]
    [InlineData("""{"accepted":true,"status":{"state":"Idle","now":"2026-08-24T09:30:00+00:00","sites":[],"apps":[],"layers":[{"isAvailable":true}]}}""")]
    public async Task AnAcknowledgementWhoseStatusIsUnusableIsNotShownAsAvailable(string acknowledgement)
    {
        var serving = ServeAsync(acknowledgement);
        var seen = new List<ServiceSnapshot>();
        await using var link = new ServiceLink(_pipeName, _waits.NoWaitAsync);
        link.Changed += (_, snapshot) =>
        {
            lock (seen)
            {
                seen.Add(snapshot);
            }
        };

        link.Start();

        var snapshot = await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Unavailable, "gave up on an answer it cannot use");

        Assert.Null(snapshot.Status);
        lock (seen)
        {
            Assert.DoesNotContain(seen, s => s.State == ServiceConnectionState.Available);
        }

        _ = serving;
    }

    /// <summary>An acknowledgement the service accepted but that cannot be used is not another protocol version, and the log line says which it was.</summary>
    [Fact]
    public async Task AnAcceptedButUnusableAcknowledgementIsLoggedAsSuchAndNotAsAnotherProtocol()
    {
        using var log = new TempLog();
        var serving = ServeAsync("""{"accepted":true,"status":{"state":"Idle","now":"2026-08-24T09:30:00+00:00"}}""");
        await using var link = new ServiceLink(_pipeName, _waits.NoWaitAsync, log.Log);

        link.Start();

        var record = await SubscriptionLineAsync(log);
        Assert.Contains("could not be used", record, StringComparison.Ordinal);
        Assert.DoesNotContain("protocol version", record, StringComparison.Ordinal);

        _ = serving;
    }

    [Fact]
    public async Task ARefusedSubscriptionIsStillLoggedAsAnotherProtocol()
    {
        using var log = new TempLog();
        var serving = ServeAsync("""{"accepted":false,"error":"ipc.version-unsupported"}""");
        await using var link = new ServiceLink(_pipeName, _waits.NoWaitAsync, log.Log);

        link.Start();

        var record = await SubscriptionLineAsync(log);
        Assert.Contains("protocol version", record, StringComparison.Ordinal);

        _ = serving;
    }

    [Fact]
    public async Task AnEventWhoseStatusIsUnusableIsNotAdoptedAndTheStreamGoesOn()
    {
        var serving = ServeAsync(
            $$"""{"accepted":true,"status":{{Whole}}}""",
            $$"""{"kind":"StatusChanged","at":"2026-08-24T09:32:00+00:00","status":{{NoLayers}}}""",
            $$"""{"kind":"StatusChanged","at":"2026-08-24T09:31:00+00:00","status":{{Later}}}""");
        var seen = new List<ServiceSnapshot>();
        await using var link = new ServiceLink(_pipeName, _waits.NoWaitAsync);
        link.Changed += (_, snapshot) =>
        {
            lock (seen)
            {
                seen.Add(snapshot);
            }
        };

        link.Start();

        // The one after the unusable one proves the loop did not stop.
        var active = await LinkWait.ForAsync(
            link, static s => s.Status?.State == "Active", "took the status after the unusable one");

        Assert.NotNull(active.Status!.Layers);
        lock (seen)
        {
            Assert.All(seen.Where(s => s.Status is not null), s => Assert.NotNull(s.Status!.Layers));
        }

        _ = serving;
    }

    [Theory]
    [InlineData("AppBlocked", "appName")]
    [InlineData("SiteBlocked", "domain")]
    public async Task ABlockWhoseStatusIsUnusableIsNotReported(string kind, string target)
    {
        var serving = ServeAsync(
            $$"""{"accepted":true,"status":{{Whole}}}""",
            $$"""{"kind":"{{kind}}","at":"2026-08-24T09:32:00+00:00","{{target}}":"thing","status":{{NoLayers}}}""",
            $$"""{"kind":"StatusChanged","at":"2026-08-24T09:31:00+00:00","status":{{Later}}}""");
        var blocks = new List<BlockEvent>();
        await using var link = new ServiceLink(_pipeName, _waits.NoWaitAsync);
        link.Blocked += (_, block) =>
        {
            lock (blocks)
            {
                blocks.Add(block);
            }
        };

        link.Start();

        await LinkWait.ForAsync(link, static s => s.Status?.State == "Active", "went past the block");

        lock (blocks)
        {
            Assert.Empty(blocks);
        }

        _ = serving;
    }

    /// <summary>The one line about the subscription; an attempt that raced the pipe's creation may have logged "cannot be reached" first.</summary>
    private static async Task<string> SubscriptionLineAsync(TempLog log)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (true)
        {
            var lines = log.Lines().Where(line => line.Contains("subscription", StringComparison.Ordinal)).ToArray();
            if (lines.Length > 0)
            {
                return Assert.Single(lines);
            }

            Assert.True(DateTime.UtcNow < limit, "No line about the subscription was written.");
            await Task.Delay(10);
        }
    }

    private Task ServeAsync(params string[] lines) => Task.Run(async () =>
    {
        await using var server = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(_stop.Token);

        using var reader = new StreamReader(server, leaveOpen: true);
        await reader.ReadLineAsync(_stop.Token);

        await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
        foreach (var line in lines)
        {
            await writer.WriteLineAsync(line.AsMemory(), _stop.Token);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, _stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
    });
}
