using System.IO.Pipes;
using Chronos.Ipc;

namespace Chronos.Service.Tests;

/// <summary>
/// What each client command does with an answer it cannot use: not JSON, the wrong shape, nothing, cut short, or missing what the command needs.
/// The service is a pipe of the test's own that writes one line and goes.
/// </summary>
public sealed class UnreadableAnswerTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string GoodStatus =
        """{"state":"Idle","now":"2026-08-24T09:30:00+00:00","coolDownMinutes":5,"sites":[],"apps":[],"layers":[]}""";

    private const string GoodConfig =
        """{"sites":[],"apps":[],"coolDownMinutes":5,"defaultSessionMinutes":60,"language":"en","verboseLogging":false,"wfpEnabled":true}""";

    private readonly string _pipeName = "chronos-test-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();

    public void Dispose() => _cts.Dispose();

    /// <summary>Every command that asks one question and prints the answer.</summary>
    public static TheoryData<string> Commands => ["status", "start", "extend", "unlock", "cancel"];

    /// <summary>Answers no command can use. The last few parse and are still no answer: an accepted one without the status, and a status without the lists the printer reads.</summary>
    public static TheoryData<string, string> Unusable => new()
    {
        { "not json", "this is not json" },
        { "wrong shape: an array", "[1,2,3]" },
        { "wrong shape: a number", "42" },
        { "wrong type for a field", """{"accepted":"yes"}""" },
        { "null", "null" },
        { "truncated", """{"accepted":true,"status":{"state":"Idle","sites":[""" },
        { "accepted without a status", """{"accepted":true}""" },
        { "status without its lists", """{"accepted":true,"status":{"state":"Idle"}}""" },
        {
            "status without layers",
            """{"accepted":true,"status":{"state":"Idle","sites":[],"apps":[],"layers":null}}"""
        },
        {
            "status without a state",
            """{"accepted":true,"status":{"sites":[],"apps":[],"layers":[]}}"""
        },
        {
            "status without sites",
            """{"accepted":true,"status":{"state":"Idle","apps":[],"layers":[]}}"""
        },
        {
            "status without apps",
            """{"accepted":true,"status":{"state":"Idle","sites":[],"layers":[]}}"""
        },
        {
            "a null site rule",
            """{"accepted":true,"status":{"state":"Idle","sites":[null],"apps":[],"layers":[]}}"""
        },
        {
            "a null application rule",
            """{"accepted":true,"status":{"state":"Idle","sites":[],"apps":[null],"layers":[]}}"""
        },
        {
            "a null layer",
            """{"accepted":true,"status":{"state":"Idle","sites":[],"apps":[],"layers":[null]}}"""
        },
        {
            "a layer without a name",
            """{"accepted":true,"status":{"state":"Idle","sites":[],"apps":[],"layers":[{"isAvailable":true}]}}"""
        },
    };

    public static TheoryData<string, string, string> EveryCommandWithEveryUnusableAnswer()
    {
        var all = new TheoryData<string, string, string>();
        foreach (var command in new[] { "status", "start", "extend", "unlock", "cancel" })
        {
            foreach (var row in Unusable)
            {
                all.Add(command, (string)row[0], (string)row[1]);
            }
        }

        return all;
    }

    [Theory]
    [MemberData(nameof(EveryCommandWithEveryUnusableAnswer))]
    public async Task ACommandAnsweredWithSomethingUnusableReportsItAndExitsWithThree(
        string command, string what, string line)
    {
        _ = what;
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(command, line, output, error);

        Assert.Equal(3, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("cannot read", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not reachable", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAnsweredWithoutTheConfigurationItAsksForExitsWithThree()
    {
        // GetConfig is what "status" sends, and the configuration is half of what it prints.
        var error = new StringWriter();

        var exitCode = await RunAsync("status", $$"""{"accepted":true,"status":{{GoodStatus}}}""", TextWriter.Null, error);

        Assert.Equal(3, exitCode);
        Assert.Contains("cannot read", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"apps":[],"coolDownMinutes":5}""")]
    [InlineData("""{"sites":[],"coolDownMinutes":5}""")]
    [InlineData("""{"sites":[null],"apps":[],"coolDownMinutes":5}""")]
    [InlineData("""{"sites":[],"apps":[null],"coolDownMinutes":5}""")]
    public async Task StatusAnsweredWithAConfigurationMissingItsListsExitsWithThree(string config)
    {
        // Its own rows: the status beside it is whole, so only the configuration can be the reason.
        var error = new StringWriter();

        var exitCode = await RunAsync(
            "status", $$"""{"accepted":true,"status":{{GoodStatus}},"config":{{config}}}""", TextWriter.Null, error);

        Assert.Equal(3, exitCode);
        Assert.Contains("cannot read", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("extend")]
    [InlineData("unlock")]
    [InlineData("cancel")]
    public async Task ASessionCommandNeedsNoConfigurationInItsAnswer(string command)
    {
        // The other side of the check above: those four leave the configuration null by contract.
        var output = new StringWriter();

        var exitCode = await RunAsync(command, $$"""{"accepted":true,"status":{{GoodStatus}}}""", output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Contains("State:  Idle", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAnsweredInFullPrintsIt()
    {
        var output = new StringWriter();

        var exitCode = await RunAsync(
            "status", $$"""{"accepted":true,"status":{{GoodStatus}},"config":{{GoodConfig}}}""", output, TextWriter.Null);

        Assert.Equal(0, exitCode);
        Assert.Contains("Config: 0 sites, 0 apps", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task ARefusalNeedsNoStatusAndKeepsItsExitCode(string command)
    {
        // Only an accepted answer has to carry the status; a refusal says why and leaves it out.
        var error = new StringWriter();

        var exitCode = await RunAsync(command, """{"accepted":false,"error":"request.empty"}""", TextWriter.Null, error);

        Assert.Equal(1, exitCode);
        Assert.Equal("Request is empty.", error.ToString().Trim());
    }

    [Theory]
    [MemberData(nameof(Unusable))]
    public async Task WatchAnsweredWithSomethingUnusableReportsItAndExitsWithThree(string what, string line)
    {
        _ = what;
        var output = new StringWriter();
        var error = new StringWriter();

        var serving = ServeAsync(line);
        var exitCode = await Cli.Program
            .WatchAsync(new IpcClient(_pipeName), output, error, _cts.Token)
            .WaitAsync(Patience, CancellationToken.None);
        await serving.WaitAsync(Patience, CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.DoesNotContain("Watching", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("cannot read", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"at":"2026-08-24T09:30:00+00:00"}""")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task AnEventThatCannotBeUsedIsSkippedAndTheNextOneStillArrives(string unusable)
    {
        // One event lost is not a reason to stop watching, and one without a kind has nothing to print; it must not throw in the printer.
        const string next = """{"kind":"AppBlocked","at":"2026-08-24T09:30:00+00:00","appName":"game.exe"}""";
        var serving = ServeAsync($$"""{"accepted":true,"status":{{GoodStatus}}}""", unusable, next);

        await using var subscription = await new IpcClient(_pipeName).SubscribeAsync(_cts.Token);
        var read = new List<IpcEvent>();
        await foreach (var published in subscription.ReadEventsAsync(_cts.Token).WithCancellation(_cts.Token))
        {
            read.Add(published);
        }

        await serving.WaitAsync(Patience, CancellationToken.None);

        Assert.Equal("game.exe", Assert.Single(read).AppName);
    }

    [Fact]
    public void PrintingAnAnswerWhoseWarningsAreNullPrintsTheRest()
    {
        // "warnings": null on the wire lands in a property the type says is never null.
        var output = new StringWriter();
        var error = new StringWriter();

        Cli.Program.Print(new IpcResponse { Accepted = false, Error = IpcCodes.RequestEmpty, Warnings = null! }, output, error);

        Assert.DoesNotContain("warning", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("Request is empty.", error.ToString().Trim());
    }

    private async Task<int> RunAsync(string verb, string line, TextWriter output, TextWriter error)
    {
        var serving = ServeAsync(line);
        var exitCode = await Cli.Program
            .ClientCommandAsync(
                new IpcClient(_pipeName),
                new IpcRequest { Command = Cli.Program.MapCommand(verb)! },
                output,
                error)
            .WaitAsync(Patience, CancellationToken.None);
        await serving.WaitAsync(Patience, CancellationToken.None);

        return exitCode;
    }

    /// <summary>
    /// Takes one connection, reads the request, writes the lines it was given - the last without
    /// its line ending, as a service that died mid-write would leave it - and goes.
    /// </summary>
    private Task ServeAsync(params string[] lines) => Task.Run(async () =>
    {
        await using var server = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(_cts.Token);

        using var reader = new StreamReader(server, leaveOpen: true);
        await reader.ReadLineAsync(_cts.Token);

        await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
        for (var i = 0; i < lines.Length; i++)
        {
            await (i < lines.Length - 1
                ? writer.WriteLineAsync(lines[i].AsMemory(), _cts.Token)
                : writer.WriteAsync(lines[i].AsMemory(), _cts.Token));
        }

        server.WaitForPipeDrain();
    });
}
