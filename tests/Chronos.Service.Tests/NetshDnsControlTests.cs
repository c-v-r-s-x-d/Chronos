using System.Diagnostics;
using System.Globalization;
using System.Net;
using Chronos.Service.Dns;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

/// <summary>
/// The commands that take an interface over and hand it back, and how an exit code is read.
/// Nothing runs netsh: commands are built and read back as text, and process starting is pointed at cmd.exe.
/// </summary>
public sealed class NetshDnsControlTests
{
    [Fact]
    public void SetFirst_NamesTheInterfaceByIndexAndSkipsValidation()
    {
        var command = NetshCommands.SetFirst(22, "127.0.0.1");

        // The index, because the adapter name may be non-Latin. validate=no, because the resolver may not be up yet and a validating netsh waits seconds for it.
        Assert.Equal(
            "interface ipv4 set dnsservers name=22 source=static address=127.0.0.1 validate=no",
            command);
    }

    [Fact]
    public void AddNext_PutsTheOriginalServerSecond()
    {
        Assert.Equal(
            "interface ipv4 add dnsservers name=22 address=8.8.8.8 index=2 validate=no",
            NetshCommands.AddNext(22, "8.8.8.8", 2));
    }

    [Fact]
    public void AddFirst_PutsTheResolverInFrontOfTheListTheInterfaceHas()
    {
        Assert.Equal(
            "interface ipv4 add dnsservers name=22 address=127.0.0.1 index=1 validate=no",
            NetshCommands.AddFirst(22, "127.0.0.1"));
    }

    [Fact]
    public void AddFirst_TakesNothingButAnAddressOfThisMachine()
    {
        // Position 1 is the resolver's; an outside server there would push it down the list.
        Assert.Throws<ArgumentException>(() => NetshCommands.AddFirst(22, "8.8.8.8"));
        Assert.Throws<ArgumentNullException>(() => NetshCommands.AddFirst(22, null!));
        Assert.Throws<ArgumentException>(() => NetshCommands.AddFirst(22, "nonsense"));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetshCommands.AddFirst(0, "127.0.0.1"));
    }

    [Fact]
    public void Dhcp_PutsTheInterfaceBackToItsDhcpMode()
    {
        Assert.Equal(
            "interface ipv4 set dnsservers name=22 source=dhcp",
            NetshCommands.Dhcp(22));
    }

    [Fact]
    public void EveryCommandTakesTheLowestIndexWindowsGivesAnAdapter()
    {
        // Windows numbers interfaces from 1, so 1 is an adapter and has to be accepted.
        Assert.Contains("name=1 ", NetshCommands.SetFirst(1, "127.0.0.1"));
        Assert.Contains("name=1 ", NetshCommands.AddNext(1, "8.8.8.8", 2));
        Assert.Contains("name=1 ", NetshCommands.Dhcp(1));
    }

    [Fact]
    public void NoCommandWillNameAnIndexThatIsNoAdapter()
    {
        // 0 is the unspecified index; netsh would read "name=0" as an adapter name and fail.
        Assert.Throws<ArgumentOutOfRangeException>(() => NetshCommands.SetFirst(0, "127.0.0.1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetshCommands.AddNext(0, "8.8.8.8", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetshCommands.Dhcp(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetshCommands.Dhcp(-1));
    }

    [Fact]
    public void AddNext_WillNotTakeThePositionThatBelongsToTheResolver()
    {
        // Position 1 is SetFirst's. An add that took it would push 127.0.0.1 down the list.
        Assert.Throws<ArgumentOutOfRangeException>(() => NetshCommands.AddNext(22, "8.8.8.8", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetshCommands.AddNext(22, "8.8.8.8", 0));
    }

    [Fact]
    public void AddNext_CarriesThePositionItIsGiven()
    {
        Assert.Contains(" index=3 ", NetshCommands.AddNext(22, "8.8.8.8", 3));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("8.8.8.8 8.8.4.4")]
    [InlineData("8.8.8.8;shutdown")]
    [InlineData("")]
    [InlineData("   ")]
    public void NoCommandWillCarrySomethingThatIsNotAnAddress(string server)
    {
        // A command is handed to netsh as words, so a server with a space would arrive as two arguments.
        Assert.ThrowsAny<ArgumentException>(() => NetshCommands.SetFirst(22, server));
        Assert.ThrowsAny<ArgumentException>(() => NetshCommands.AddNext(22, server, 2));
    }

    [Fact]
    public void NoCommandWillCarryNoServerAtAll()
    {
        Assert.Throws<ArgumentNullException>(() => NetshCommands.SetFirst(22, null!));
        Assert.Throws<ArgumentNullException>(() => NetshCommands.AddNext(22, null!, 2));
    }

    [Fact]
    public void NoCommandWillCarryAnIPv6Address()
    {
        // This one parses, so the family check refuses it: "interface ipv4" takes IPv4 addresses only and the resolver binds 127.0.0.1.
        Assert.Throws<ArgumentException>(() => NetshCommands.SetFirst(22, "2001:4860:4860::8888"));
        Assert.Throws<ArgumentException>(() => NetshCommands.AddNext(22, "2001:4860:4860::8888", 2));
    }

    [Fact]
    public void SetStatic_PutsTheResolverFirstAndTheServersItFoundAfterIt()
    {
        var (control, netsh, _) = Build(0, 0, 0);

        Assert.True(control.SetStatic(22, ["127.0.0.1", "8.8.8.8", "8.8.4.4"]));

        // The outside servers first, the resolver last, so a failing step leaves an outside server in the list and never 127.0.0.1 alone.
        Assert.Equal(
            [
                "interface ipv4 set dnsservers name=22 source=static address=8.8.8.8 validate=no",
                "interface ipv4 add dnsservers name=22 address=8.8.4.4 index=2 validate=no",
                "interface ipv4 add dnsservers name=22 address=127.0.0.1 index=1 validate=no",
            ],
            netsh.Commands);
    }

    [Fact]
    public void SetStatic_RunsTheSetAndOneAddForTheResolverAndTheServerTheInterfaceCameWith()
    {
        // The resolver first, the interface's own server behind it: one add, at position 2.
        var (control, netsh, _) = Build(0, 0);

        Assert.True(control.SetStatic(22, ["127.0.0.1", "8.8.8.8"]));

        Assert.Equal(
            [
                "interface ipv4 set dnsservers name=22 source=static address=8.8.8.8 validate=no",
                "interface ipv4 add dnsservers name=22 address=127.0.0.1 index=1 validate=no",
            ],
            netsh.Commands);
    }

    [Fact]
    public void SetStatic_PutsSeveralAddressesOfThisMachineInFrontInTheirOwnOrder()
    {
        var (control, netsh, _) = Build(0, 0, 0);

        Assert.True(control.SetStatic(22, ["127.0.0.1", "127.0.0.53", "8.8.8.8"]));

        Assert.Equal(
            [
                "interface ipv4 set dnsservers name=22 source=static address=8.8.8.8 validate=no",
                "interface ipv4 add dnsservers name=22 address=127.0.0.1 index=1 validate=no",
                "interface ipv4 add dnsservers name=22 address=127.0.0.53 index=2 validate=no",
            ],
            netsh.Commands);
    }

    [Fact]
    public void SetStatic_KeepsALoopbackAddressLaterInTheListWhereItWas()
    {
        // Only the leading ones go in front; a restore of a user's own list keeps its order.
        var (control, netsh, _) = Build(0, 0);

        Assert.True(control.SetStatic(22, ["8.8.8.8", "127.0.0.1"]));

        Assert.Equal(
            [
                "interface ipv4 set dnsservers name=22 source=static address=8.8.8.8 validate=no",
                "interface ipv4 add dnsservers name=22 address=127.0.0.1 index=2 validate=no",
            ],
            netsh.Commands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SetStatic_AFailureAtAnyStepNeverLeavesTheResolverAlone(int failing)
    {
        // The commands applied to a list the way netsh applies them, with step `failing` refused (3: none). An outside server stays.
        string[] wanted = ["127.0.0.1", "192.168.1.1", "192.168.1.2"];
        var (control, netsh, _) = Build([.. Enumerable.Range(0, Math.Min(failing + 1, 3)).Select(step => step == failing ? 1 : 0)]);

        var done = control.SetStatic(22, wanted);

        var list = Applied(["10.0.0.1"], netsh.Commands.Take(failing));
        Assert.Equal(failing == 3, done);
        Assert.Contains(list, server => !IPAddress.IsLoopback(IPAddress.Parse(server)));
        if (done)
        {
            Assert.Equal(wanted, list);
        }
    }

    [Fact]
    public void SetStatic_RunsNothingButTheSetForASingleServer()
    {
        // The loop that places the rest of the list runs no times for a list of one.
        var (control, netsh, _) = Build(0);

        Assert.True(control.SetStatic(22, ["8.8.8.8"]));

        Assert.Equal(
            "interface ipv4 set dnsservers name=22 source=static address=8.8.8.8 validate=no",
            Assert.Single(netsh.Commands));
    }

    [Fact]
    public void SetStatic_WillNotLeaveAnInterfaceResolvingThroughThisMachineAlone()
    {
        // A working outside server has to stay in the list. A list holding only the resolver leaves the machine resolving nothing once the service stops, so it is refused before any command runs.
        // A DHCP interface handed no servers reads as a list of none, so a caller can build exactly this.
        var (control, netsh, _) = Build();

        Assert.Throws<ArgumentException>(() => control.SetStatic(22, ["127.0.0.1"]));
        Assert.Empty(netsh.Commands);
    }

    [Fact]
    public void SetStatic_WillNotTakeAListOfNothingButAddressesOfThisMachine()
    {
        // All of 127.0.0.0/8 is this machine, and a second copy of the same nothing is still nothing.
        var (control, netsh, _) = Build();

        Assert.Throws<ArgumentException>(() => control.SetStatic(22, ["127.0.0.1", "127.0.0.53"]));
        Assert.Empty(netsh.Commands);
    }

    [Fact]
    public void SetStatic_TakesAListTheMomentOneServerInItIsSomewhereElse()
    {
        // One outside server anywhere in the list is enough; it need not be first.
        var (control, netsh, _) = Build(0, 0);

        Assert.True(control.SetStatic(22, ["127.0.0.1", "8.8.8.8"]));
        Assert.Equal(2, netsh.Commands.Count);
    }

    [Fact]
    public void SetStatic_StopsAtASetThatFails()
    {
        var (control, netsh, _) = Build(1);

        Assert.False(control.SetStatic(22, ["127.0.0.1", "8.8.8.8"]));

        // And the add is not run: an interface whose list was not replaced must not have a second
        // server appended to whatever list it still has.
        Assert.Single(netsh.Commands);
    }

    [Fact]
    public void SetStatic_StopsAtAnAddThatFails()
    {
        var (control, netsh, _) = Build(0, 1, 0);

        Assert.False(control.SetStatic(22, ["127.0.0.1", "8.8.8.8", "8.8.4.4"]));

        Assert.Equal(2, netsh.Commands.Count);
    }

    [Fact]
    public void SetStatic_WillNotLeaveAnInterfaceWithNoResolverAtAll()
    {
        // netsh reads a static source with no address as "resolves through nothing"; the empty list is what is refused.
        var (control, netsh, _) = Build();

        Assert.Throws<ArgumentException>(() => control.SetStatic(22, []));
        Assert.Empty(netsh.Commands);
    }

    [Fact]
    public void SetStatic_RefusesNoListAtAll()
    {
        var (control, netsh, _) = Build();

        Assert.Throws<ArgumentNullException>(() => control.SetStatic(22, null!));
        Assert.Empty(netsh.Commands);
    }

    [Fact]
    public void SetStatic_RunsNothingAtAllWhenAServerLaterInTheListIsNotAnAddress()
    {
        // Every command is built before the first runs; otherwise a late failure leaves the interface pointed at 127.0.0.1 with its own server dropped.
        var (control, netsh, _) = Build(0, 0);

        Assert.ThrowsAny<ArgumentException>(() => control.SetStatic(22, ["127.0.0.1", "not-an-address"]));
        Assert.Empty(netsh.Commands);
    }

    [Fact]
    public void SetStatic_RunsNothingForAnIndexThatNamesNoAdapter()
    {
        var (control, netsh, _) = Build(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => control.SetStatic(0, ["127.0.0.1"]));
        Assert.Empty(netsh.Commands);
    }

    [Fact]
    public void RestoreDhcp_RunsTheOneCommandThatHandsTheInterfaceBack()
    {
        var (control, netsh, _) = Build(0);

        Assert.True(control.RestoreDhcp(22));

        Assert.Equal(
            "interface ipv4 set dnsservers name=22 source=dhcp",
            Assert.Single(netsh.Commands));
    }

    [Fact]
    public void RestoreDhcp_RunsNothingForAnIndexThatNamesNoAdapter()
    {
        var (control, netsh, _) = Build(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => control.RestoreDhcp(0));
        Assert.Empty(netsh.Commands);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    [InlineData(5, false)]
    public void ZeroIsTheOnlyExitCodeThatMeansTheInterfaceWasChanged(int exitCode, bool expected)
    {
        var (control, _, _) = Build(exitCode);

        Assert.Equal(expected, control.RestoreDhcp(22));
    }

    [Fact]
    public void ACallThatNeverAnsweredIsReportedAsACodeNetshItselfCannotReturn()
    {
        // A killed netsh has to be a failure, and it must not collide with one of netsh's own
        // codes, all of which are counts of something.
        Assert.Equal(-1, NetshDnsControl.NoAnswer);
    }

    [Fact]
    public void WhatNetshSaidNeverDecidesAnything()
    {
        // netsh output is localized. It says success while the exit code says failure: the code wins.
        var refused = Build(1);
        refused.Netsh.Output = "OK";
        Assert.False(refused.Control.RestoreDhcp(22));

        // And the other way round, with the words of a failure over an exit code of zero.
        var accepted = Build(0);
        accepted.Netsh.Output = "Element not found.";
        Assert.True(accepted.Control.RestoreDhcp(22));
    }

    [Fact]
    public void WhatNetshSaidGoesToTheLogAtDebugAndNoHigher()
    {
        var (control, netsh, log) = Build(0);
        netsh.Output = "DNS-servery uspeshno nastroeny";

        Assert.True(control.RestoreDhcp(22));

        var said = Assert.Single(log.Entries, entry => entry.Message.Contains("DNS-servery", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, said.Level);

        // A run that worked is not a warning, whatever the localised sentence says.
        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public void AFailedCommandIsLoggedWithItsNumberAndTheCommandThatFailed()
    {
        var (control, netsh, log) = Build(1);
        netsh.Output = "Zapros ne podderzhivaetsya";

        Assert.False(control.RestoreDhcp(22));

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("interface ipv4 set dnsservers name=22 source=dhcp", warning.Message, StringComparison.Ordinal);
        Assert.Contains("1", warning.Message, StringComparison.Ordinal);

        // The reason a reader can act on is the number; the sentence is localised and goes to the Debug line.
        Assert.DoesNotContain("Zapros", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallOntoTheMachineIsGivenFifteenSecondsToAnswer()
    {
        // Long enough that a busy machine is not mistaken for a stuck one; finite because it runs during service start and stop.
        Assert.Equal(TimeSpan.FromSeconds(15), NetshDnsControl.CallTimeout);
    }

    [Fact]
    public void TheNetshThatRunsIsTheOneInTheSystemDirectory()
    {
        // The full path, not the name: as LocalSystem, a directory earlier in PATH would otherwise decide what "netsh" means.
        Assert.Equal("netsh.exe", Path.GetFileName(NetshDnsControl.NetshPath));
        Assert.True(Path.IsPathRooted(NetshDnsControl.NetshPath));
        Assert.True(File.Exists(NetshDnsControl.NetshPath), NetshDnsControl.NetshPath);
    }

    [Fact]
    public void TheLastOfTheOutputIsWaitedForAndNotWaitedForLong()
    {
        // A second ceiling on the output. The exit code is already known, so missing output costs a poorer log line only; it is short so a child that outlived the pipe cannot hold start-up open.
        Assert.Equal(TimeSpan.FromSeconds(5), NetshDnsControl.DrainTimeout);
    }

    [Fact]
    public void StartOf_StartsTheNetshOfTheSystemDirectoryWithNoShellAndNoWindow()
    {
        var start = NetshDnsControl.StartOf(NetshCommands.Dhcp(22));

        Assert.Equal(NetshDnsControl.NetshPath, start.FileName);

        // No shell, so neither PATH nor a file association decides what runs; no window, because a service has nobody to see it.
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
    }

    [Fact]
    public void StartOf_TakesBothOfTheStreamsTheProgramWritesOn()
    {
        // netsh puts some refusals on the error stream; an unread stream goes to a console the service lacks, so the Debug line would miss half.
        var start = NetshDnsControl.StartOf(NetshCommands.Dhcp(22));

        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
    }

    [Fact]
    public void StartOf_HandsOverEveryWordOfTheCommandAsAnArgumentOfItsOwn()
    {
        var start = NetshDnsControl.StartOf(NetshCommands.SetFirst(22, "8.8.8.8"));

        // One argument per word, passed as a list so nothing depends on quoting rules.
        Assert.Equal(
            [
                "interface", "ipv4", "set", "dnsservers", "name=22", "source=static",
                "address=8.8.8.8", "validate=no",
            ],
            start.ArgumentList);

        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void StartOf_IsNotGivenACommandToBuildFrom()
    {
        Assert.Throws<ArgumentNullException>(() => NetshDnsControl.StartOf(null!));
    }

    [Fact]
    public void Run_ReportsTheCodeTheProgramItRanExitedWith()
    {
        // Seven, so a run that answered "1" or "0" for everything would not pass.
        // Five seconds, read as milliseconds; seconds passed instead would leave five milliseconds to start.
        var result = NetshDnsControl.Run(Harmless("/c", "exit", "7"), TimeSpan.FromSeconds(5));

        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public void Run_ReportsWhatTheProgramSaidOnEitherStream()
    {
        var result = NetshDnsControl.Run(Harmless("/c", "echo out& echo err 1>&2"), TimeSpan.FromSeconds(5));

        // Both: netsh puts the interesting half on the error stream, and the log line is read from both.
        Assert.Contains("out", result.Output, StringComparison.Ordinal);
        Assert.Contains("err", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_StopsAProgramThatOutstaysItsCeilingAndSaysNothingAnswered()
    {
        // A program waiting for a keystroke that never comes: its input is a pipe nothing writes to. The ceiling is short so the suite does not stall.
        var start = Harmless("/c", "pause");
        start.RedirectStandardInput = true;

        var result = NetshDnsControl.Run(start, TimeSpan.FromMilliseconds(200));

        Assert.Equal(NetshDnsControl.NoAnswer, result.ExitCode);

        // The message names the program and the ceiling, since a stopped call differs from a failed one in the log.
        Assert.Contains("cmd.exe", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_IsNotGivenAProgramToStart()
    {
        Assert.Throws<ArgumentNullException>(() => NetshDnsControl.Run(null!, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ThereIsNoControlWithoutALogOrWithoutSomethingToRun()
    {
        Assert.Throws<ArgumentNullException>(() => new NetshDnsControl(null!));
        Assert.Throws<ArgumentNullException>(
            () => new NetshDnsControl(new CapturingLogger<NetshDnsControl>(), null!));
    }

    /// <summary>A program that is not netsh, started the way netsh is. cmd.exe exits with a number, writes on either stream or waits forever.</summary>
    private static ProcessStartInfo Harmless(params string[] arguments)
    {
        var start = new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <summary>What a list becomes after these commands, read the way netsh applies them.</summary>
    private static List<string> Applied(IEnumerable<string> start, IEnumerable<string> commands)
    {
        var list = new List<string>(start);

        foreach (var command in commands)
        {
            var words = command.Split(' ')
                .Select(word => word.Split('='))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1]);

            if (words.ContainsKey("source"))
            {
                list = [words["address"]];
            }
            else
            {
                var at = int.Parse(words["index"], CultureInfo.InvariantCulture) - 1;
                list.Insert(Math.Min(at, list.Count), words["address"]);
            }
        }

        return list;
    }

    private static Built Build(params int[] exitCodes)
    {
        var netsh = new Netsh(exitCodes);
        var log = new CapturingLogger<NetshDnsControl>();

        return new Built(new NetshDnsControl(log, netsh.Run), netsh, log);
    }

    private sealed record Built(NetshDnsControl Control, Netsh Netsh, CapturingLogger<NetshDnsControl> Log);

    /// <summary>netsh, replaced by a record of the commands run and the codes it answers with, in order. Running out of codes throws.</summary>
    private sealed class Netsh(int[] exitCodes)
    {
        private readonly Queue<int> _exitCodes = new(exitCodes);

        public List<string> Commands { get; } = [];

        public string Output { get; set; } = string.Empty;

        public NetshDnsControl.NetshResult Run(string command)
        {
            Commands.Add(command);

            if (_exitCodes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"netsh was asked to run '{command}', which is one command more than this test allows.");
            }

            return new NetshDnsControl.NetshResult(_exitCodes.Dequeue(), Output);
        }
    }
}
