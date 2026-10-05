using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Chronos.Cli.Commands;
using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Ipc;
using Chronos.Service.Configuration;
using Chronos.Service.Dns;
using Chronos.Service.Sites;
using Chronos.Service.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

/// <summary>
/// The wiring install, uninstall, recover and diag get on a real machine: what the probe counts as an answer and what clearing the state clears.
/// Everything else is read back through a test collaborator, so the lambdas that build it need their own test.
/// The state file is under the temp directory and the probe uses a pipe of this test's own; only the three ways of not answering cost wall clock.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SetupContextTests : IDisposable
{
    /// <summary>The probe's patience, shortened; the question is only whether anything is on the other end.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(300);

    private static readonly DateTimeOffset Moment = new(2026, 8, 30, 7, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-setup-" + Guid.NewGuid().ToString("N"));

    private readonly string _pipeName = "chronos-setup-" + Guid.NewGuid().ToString("N");

    private readonly CancellationTokenSource _cts = new();

    private readonly ChronosPaths _paths;

    /// <summary>The request line the probe sent, as the far end read it off the pipe.</summary>
    private string? _asked;

    public SetupContextTests()
    {
        _paths = new ChronosPaths(Path.Combine(_root, "data"), Path.Combine(_root, "user"));
        _paths.EnsureDataDirectoryExists();
    }

    /// <summary>What the far end of the pipe does with the connection it was given.</summary>
    private enum Answer
    {
        /// <summary>A well-formed "no". The service is there and disagrees.</summary>
        Refusal,

        /// <summary>A line that is not the protocol: a service dying mid-write, or an older build.</summary>
        Garbage,

        /// <summary>The connection taken and held, and not one byte written back.</summary>
        Silence,

        /// <summary>The connection taken and dropped without a word.</summary>
        HangUp,
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        TestDirectory.Delete(_root);
    }

    [Fact]
    public async Task ARefusalIsAnAnswer()
    {
        // The question is whether anything is on the other end of the pipe, not whether it agrees: a service that answers still has something running.
        Listen(Answer.Refusal);

        Assert.True(await Setup().AnswersAsync(CancellationToken.None));

        // GetStatus and not something else: it reads nothing off the disk to answer, so a service
        // whose configuration file is the broken part still answers it.
        Assert.Contains("GetStatus", _asked, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15000)]
    public async Task AServiceThatTakesTheConnectionAndSaysNothingIsNotAnswering()
    {
        // The machine recover exists for: the server serves each connection on its own task, so a jammed dispatcher still accepts in milliseconds and never writes.
        // Unbounded, the command would wait until the scheduler kills it at five minutes with nothing cleaned.
        Listen(Answer.Silence);

        var watch = Stopwatch.StartNew();
        var answered = await Setup().AnswersAsync(CancellationToken.None);
        watch.Stop();

        Assert.False(answered);

        // Bounded by the probe's patience, not by anything the far end does.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the probe took {watch.Elapsed}");
    }

    [Fact]
    public async Task AnAnswerThisBuildCannotReadIsNotAnAnswer()
    {
        // A service dying mid-write, or speaking another protocol; a JsonException out of Main would leave a boot-time machine blocked.
        Listen(Answer.Garbage);

        Assert.False(await Setup().AnswersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task APipeThatIsDroppedWithoutAWordIsNotAnAnswer()
    {
        Listen(Answer.HangUp);

        Assert.False(await Setup().AnswersAsync(CancellationToken.None));
    }

    [Fact(Timeout = 15000)]
    public async Task NothingListeningIsNotAnAnswer()
    {
        // No host at all: the name nothing is on. Bounded by the same patience, because a machine
        // whose service was never registered is not a machine to spend the connect ceiling on.
        var watch = Stopwatch.StartNew();
        var answered = await Setup().AnswersAsync(CancellationToken.None);
        watch.Stop();

        Assert.False(answered);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the probe took {watch.Elapsed}");
    }

    [Fact]
    public async Task ACallerThatAskedToStopIsNotToldTheServiceIsDown()
    {
        // Cancellation travels rather than turning into "it did not answer": one is a fact about the service, the other about the caller.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Setup().AnswersAsync(cancelled.Token));
    }

    [Fact]
    public void ClearingTheStateRemovesTheFileTheWiringWasPointedAt()
    {
        // Clearing state through the wiring that carries it out: a session with three hours left is what recovery must be able to remove, and no other test goes through this lambda.
        var store = new StateStore(_paths, NullLogger<StateStore>.Instance);
        store.Save(new BlockSession
        {
            Id = Guid.NewGuid(),
            StartedAt = Moment,
            EndsAt = Moment + TimeSpan.FromHours(3),
            CoolDown = TimeSpan.FromMinutes(5),
            Rules = BlockList.Empty.WithSite(new SiteRule("example.com", includeSubdomains: true)),
        });

        Assert.True(File.Exists(_paths.StateFile));

        Setup().ClearState();

        Assert.False(File.Exists(_paths.StateFile));
    }

    [Fact]
    public void TheContextCarriesThePathsItWasBuiltFor() => Assert.Same(_paths, Setup().Paths);

    [Fact]
    public void TheLoopbackProbeFindsAPortSomebodyHolds()
    {
        // An ephemeral port this test holds; never 53.
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)holder.LocalEndPoint!).Port;

        Assert.False(Setup().ProbeLoopbackPort(port).Free);
    }

    [Fact]
    public async Task TheCleanupPutsTheResolversBackThroughTheDnsParts()
    {
        // What clean, recover, install and uninstall all run.
        var dns = new DnsMachine(_paths);
        dns.TakenOver();

        var result = await SetupWith(dns).Clean(TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["9.9.9.9"], dns.Machine.Of(9).Servers);
        Assert.Null(dns.Store.Load());
    }

    [Fact]
    public void TheSecondRestoreOfUninstallPutsTheResolversBackThroughTheDnsParts()
    {
        var dns = new DnsMachine(_paths);
        dns.TakenOver();
        var output = new StringWriter();

        var result = SetupWith(dns).RestoreDns(output, TextWriter.Null);

        Assert.True(result.RemovedSomething);
        Assert.Equal(["9.9.9.9"], dns.Machine.Of(9).Servers);

        // The service has stopped by then; nothing will take them again.
        Assert.DoesNotContain("next pass", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DiagReadsTheBackupThroughTheDnsParts()
    {
        var dns = new DnsMachine(_paths);
        dns.TakenOver();

        var sources = SetupWith(dns).DnsBackups();

        Assert.NotNull(sources.File);
        Assert.NotNull(sources.Registry);
    }

    [Fact]
    public void DiagReadsTheHostsFileItWasGiven()
    {
        File.WriteAllText(HostsPath, HostsBlock.Write(string.Empty, ["0.0.0.0 example.com"]));

        Assert.Equal(["0.0.0.0 example.com"], SetupWith(new DnsMachine(_paths)).ReadHostsBlock());
    }

    [Fact]
    public async Task TheCleanupWaitsOnTheLockOfTheParts()
    {
        var dns = new DnsMachine(_paths);
        dns.TakenOver();
        dns.Lock.HeldElsewhere = true;

        var result = await SetupWith(dns).Clean(TextWriter.Null, TextWriter.Null);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(["127.0.0.1", "9.9.9.9"], dns.Machine.Of(9).Servers);
    }

    [Fact]
    public void TheSecondRestoreWaitsOnTheLockOfTheParts()
    {
        var dns = new DnsMachine(_paths);
        dns.TakenOver();
        dns.Lock.HeldElsewhere = true;

        var result = SetupWith(dns).RestoreDns(TextWriter.Null, TextWriter.Null);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(["127.0.0.1", "9.9.9.9"], dns.Machine.Of(9).Servers);
    }

    [Fact]
    public void TheRealPartsReadTheBackupUnderThePathsTheyWereGiven()
    {
        // The file answers first, so the registry of this machine is never read.
        new DnsBackupStore(_paths, new MemoryMirror(), NullLogger<DnsBackupStore>.Instance)
            .Save(new DnsBackup(Moment, [new InterfaceDnsState("{guid-9}", 9, "Ethernet", IsDhcp: false, ["9.9.9.9"])]));

        Assert.True(MachineParts.Real(_paths).OpenDnsBackups().HoldsACopy());
    }

    [Fact]
    public void TheProductKeyIsRemovedThroughThePartsTheContextWasGiven()
    {
        var dns = new DnsMachine(_paths);

        var removal = SetupWith(dns).RemoveProductKey();

        Assert.Equal(ProductKeyRemoval.Removed, removal);
        Assert.Equal(1, dns.ProductKeyRemovals);
    }

    [Fact]
    public void TheRealPartsRemoveTheMachinesProductKey()
    {
        // Pinned by identity: calling it here would reach HKLM.
        Assert.Equal((Func<ProductKeyRemoval>)ProductKey.RemoveIfEmpty, MachineParts.Real(_paths).RemoveProductKey);
    }

    [Fact]
    public void TheRealPartsTakeTheProductsSettingsLock()
    {
        // What clean, recover, install and uninstall open: the service's name, not a local one.
        var settingsLock = Assert.IsType<NamedDnsSettingsLock>(MachineParts.Real(_paths).DnsLock);

        Assert.Equal(NamedDnsSettingsLock.DefaultName, settingsLock.Name);
    }

    private string HostsPath => Path.Combine(_root, "hosts");

    private SetupContext SetupWith(DnsMachine dns)
    {
        if (!File.Exists(HostsPath))
        {
            File.WriteAllText(HostsPath, "127.0.0.1 localhost\r\n");
        }

        return SetupContext.ForThisMachine(
            _paths,
            _pipeName,
            Patience,
            new MachineParts(
                HostsPath,
                () => new FakeWfpEngine(),
                () => dns.Store,
                store => new DnsRestore(store, dns.Control, dns.Machine, NullLogger<DnsRestore>.Instance),
                dns.Lock,
                dns.RemoveProductKey));
    }

    /// <summary>One interface a dead service left on 127.0.0.1, its backup, and netsh: all fakes.</summary>
    private sealed class DnsMachine(ChronosPaths paths)
    {
        public FakeInterfaceDns Machine { get; } = new();

        public FakeDnsSettingsLock Lock { get; } = new();

        public int ProductKeyRemovals { get; private set; }

        public ProductKeyRemoval RemoveProductKey()
        {
            ProductKeyRemovals++;

            return ProductKeyRemoval.Removed;
        }

        public FakeDnsControl Control => new(Machine);

        public DnsBackupStore Store { get; } = new(paths, new MemoryMirror(), NullLogger<DnsBackupStore>.Instance);

        public void TakenOver()
        {
            Machine.Add("{guid-9}", 9, isDhcp: false, "127.0.0.1", "9.9.9.9");
            Store.Save(new DnsBackup(Moment, [new InterfaceDnsState("{guid-9}", 9, "Ethernet", IsDhcp: false, ["9.9.9.9"])]));
        }
    }

    private SetupContext Setup() => SetupContext.ForThisMachine(_paths, _pipeName, Patience);

    /// <summary>Puts the far end of the pipe up before the probe asks; the name exists when this returns.</summary>
    private void Listen(Answer answer)
    {
        var pipe = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        _ = ServeAsync(pipe, answer);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, Answer answer)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                if (answer is Answer.HangUp)
                {
                    return;
                }

                using var reader = new StreamReader(pipe, leaveOpen: true);
                _asked = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);

                if (answer is Answer.Silence)
                {
                    // Held open, not closed: a closed connection is a different answer, and the
                    // one being tested here is the connection that stays up and stays quiet.
                    await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token).ConfigureAwait(false);

                    return;
                }

                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(answer is Answer.Refusal
                    ? JsonSerializer.Serialize(
                        new IpcResponse { Accepted = false, Error = "no session is running" }, IpcJson.Options)
                    : "{ this line is not the protocol").ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                // The test finished, or the probe gave up and went away. Both are how these hosts
                // end, and neither is a failure of the test that started one.
            }
        }
    }
}
