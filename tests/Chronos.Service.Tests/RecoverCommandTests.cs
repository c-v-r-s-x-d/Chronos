using System.ComponentModel;
using System.Runtime.Versioning;
using Chronos.Cli.Commands;
using Chronos.Core.Rules;
using Chronos.Ipc;
using Chronos.Core.Sessions;
using Chronos.Service.Configuration;
using Chronos.Service.Diagnostics;
using Chronos.Service.Dns;
using Chronos.Service.Setup;
using Chronos.Service.Sites;
using Chronos.Service.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronos.Service.Tests;

/// <summary>
/// The recovery procedure a boot-time task runs. Nothing waits: the two-minute ceiling passes on a clock the test holds.
/// The state file is the one real thing, since a fake would prove the call and not the outcome.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecoverCommandTests : IDisposable
{
    private static readonly DateTimeOffset Boot = new(2026, 8, 30, 7, 0, 0, TimeSpan.Zero);

    /// <summary>The two-minute ceiling the whole procedure has to fit inside.</summary>
    private static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

    /// <summary>What must still be left of the two minutes when the waiting stops: the ceiling covers the changes coming off, so the cleanup needs time after the wait.</summary>
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(20);

    private const string InstallDirectory = @"C:\Program Files\Chronos";

    /// <summary>ERROR_FILE_NOT_FOUND: the service is registered and its executable is gone.</summary>
    private const int FileNotFound = 2;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-recover-" + Guid.NewGuid().ToString("N"));

    private readonly ServiceTestClock _clock = new(Boot);
    private readonly FakeServiceRegistration _services;
    private readonly FakeSystemEventLog _log = new();
    private readonly StateStore _state;
    private readonly ChronosPaths _paths;
    private readonly SetupContext _setup;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    /// <summary>Every step that touched the machine, in the order it was taken.</summary>
    private readonly List<string> _steps = [];

    /// <summary>When each question was put to the service, measured from the boot this began at.</summary>
    private readonly List<TimeSpan> _probesAt = [];

    /// <summary>Every wait: when it was asked for, and how long it was.</summary>
    private readonly List<(TimeSpan At, TimeSpan Span)> _waits = [];

    private readonly FakeInterfaceDns _dnsMachine = new();
    private readonly FakeDnsControl _dnsControl;
    private readonly DnsBackupStore _dnsBackups;
    private readonly FakeDnsSettingsLock _dnsLock = new();

    private int _cleanCalls;
    private int _cleanExit;
    private bool _cleanThrows;
    private bool _clearStateFails;
    private bool _serviceAnswers;

    /// <summary>
    /// What one question costs. Zero for a machine that refuses the connection at once; on the
    /// machine this command is about it is the whole of the probe's patience, because the service
    /// takes the connection and then never writes a line.
    /// </summary>
    private TimeSpan _probeCost;

    /// <summary>
    /// When the service finishes coming up: at that point on the clock the manager reports it
    /// running and the pipe answers, because both together are what this command calls working.
    /// </summary>
    private TimeSpan? _workingAfter;

    public RecoverCommandTests()
    {
        _services = new FakeServiceRegistration(_steps);
        _paths = new ChronosPaths(Path.Combine(_root, "data"), Path.Combine(_root, "user"));
        _paths.EnsureDataDirectoryExists();
        _state = new StateStore(_paths, NullLogger<StateStore>.Instance);
        _dnsControl = new FakeDnsControl(_dnsMachine, _steps);
        _dnsBackups = new DnsBackupStore(_paths, new MemoryMirror(), NullLogger<DnsBackupStore>.Instance);

        _setup = new SetupContext(
            _services,
            ServiceAnswersAsync,
            new FakeTaskRegistration(_steps),
            _log,
            new FakeAutostartEntry(_steps),
            _paths,
            ServiceDefinition.Chronos(Path.Combine(InstallDirectory, "Chronos.Service.exe")),
            Path.Combine(InstallDirectory, "chronos.exe"),
            CleanAsync,
            ClearState,
            _ => throw new InvalidOperationException("Recovery does not create the data directory."),

            // The sources of the diagnostic package. Recovery reads none of them, and a fake that answered would hide it if it started to.
            () => throw new InvalidOperationException("Recovery does not list the filters."),
            () => throw new InvalidOperationException("Recovery does not read the hosts block."),
            _ => throw new InvalidOperationException("Recovery does not look at port 53."),
            () => throw new InvalidOperationException("Recovery does not read the DNS settings."),
            _ => throw new InvalidOperationException("Recovery does not try port 53."),
            () => throw new InvalidOperationException("Recovery reads the DNS backup through the cleanup alone."),
            (_, _) => throw new InvalidOperationException("Recovery restores DNS through the cleanup alone."),
            () => throw new InvalidOperationException("Recovery does not touch the product key."));
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Recover_WithAServiceThatAnswers_ChangesNothing()
    {
        _services.State = ServiceRunState.Running;
        _serviceAnswers = true;

        var exit = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(0, _cleanCalls);

        // Nothing was cleaned, nothing was started and the state was not cleared: this list is
        // every way the command can touch a machine, and it is empty.
        Assert.Empty(_steps);

        // No entry on the healthy path: the task runs at every boot, and a daily "all is well" would be filtered out of the log.
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Recover_WithAServiceThatIsRunningButAnswersNothing_ClearsTheMachine()
    {
        // The shape this command exists for: the process is alive, the filters are in force, and
        // nothing inside it will ever take them off.
        _services.State = ServiceRunState.Running;
        _serviceAnswers = false;

        var exit = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(1, _cleanCalls);
        Assert.Contains("clear-state", _steps, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Recover_WaitsForAServiceThatIsStillComingUp()
    {
        // A boot-time task starts before the service has finished starting. Cleaning then would
        // take the block off a machine whose installation is perfectly fine.
        _services.State = ServiceRunState.Starting;
        _workingAfter = TimeSpan.FromSeconds(30);

        var exit = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(0, _cleanCalls);

        // Returned the moment it answered, not at the ceiling: two minutes is what this command may
        // spend, not what it does spend.
        Assert.Equal(TimeSpan.FromSeconds(30), _clock.Elapsed);
    }

    [Fact]
    public async Task Recover_GivesUpAfterTwoMinutesAndNotLater()
    {
        _services.State = ServiceRunState.Stopped;
        _serviceAnswers = false;

        await RunAsync();

        // The wait has a ceiling: a machine that boots into a broken installation is unusable until this returns.
        Assert.True(_clock.Elapsed <= TimeSpan.FromMinutes(2), $"waited {_clock.Elapsed}");
        Assert.Equal(1, _cleanCalls);
    }

    [Fact]
    public async Task Recover_StopsWaitingWithEnoughOfTheTwoMinutesLeftToPutTheMachineRight()
    {
        _services.State = ServiceRunState.Stopped;
        _serviceAnswers = false;

        await RunAsync();

        // The ceiling is on the whole procedure, not the wait: taking the filters off and rewriting the hosts file is work, and a wait that spent all two minutes would leave it outside.
        Assert.True(_clock.Elapsed <= TwoMinutes - CleanupBudget, $"waited {_clock.Elapsed}");
        Assert.Equal(1, _cleanCalls);
    }

    [Fact]
    public async Task Recover_AsksNoQuestionItHasNoLongerTheTimeToWaitForTheAnswerTo()
    {
        _services.State = ServiceRunState.Stopped;
        _serviceAnswers = false;

        await RunAsync();

        // The deadline is checked before a round trip, not after: a question put on the deadline takes its connect and wait out of the cleanup's time.
        Assert.NotEmpty(_probesAt);
        Assert.All(_probesAt, at => Assert.True(at < TwoMinutes - CleanupBudget, $"asked at {at}"));
    }

    [Fact]
    public async Task Recover_AsksTheServiceAgainFiveSecondsLaterAndNotHalfAMinuteLater()
    {
        // The interval is short so the answer is not stale and a good outcome is reached at the speed of the service, not of the polling.
        _services.State = ServiceRunState.Starting;
        _workingAfter = TimeSpan.FromSeconds(5);

        var exit = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(TimeSpan.FromSeconds(5), _clock.Elapsed);
    }

    [Fact]
    public async Task Recover_NeverWaitsLongerThanWhatIsLeftOfTheWait()
    {
        // Asking is not free: a service that takes the connection and then thinks costs seconds of the window per question, and polls stop landing on multiples of five.
        _services.State = ServiceRunState.Stopped;
        _serviceAnswers = false;
        _probeCost = TimeSpan.FromSeconds(3);

        await RunAsync();

        // A whole poll waited on top of a nearly-spent window is overshoot out of the cleanup's time.
        Assert.NotEmpty(_waits);
        Assert.All(_waits, wait => Assert.True(
            wait.At + wait.Span <= TwoMinutes - CleanupBudget,
            $"a wait asked for at {wait.At} ran to {wait.At + wait.Span}"));

        // And the last one was the short one, which is the whole of what that clamp is for.
        Assert.Contains(_waits, wait => wait.Span < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Recover_StartsAServiceThatIsStoppedRatherThanWaitOutTheCeilingForNobody()
    {
        // The manager restarts a service that failed; one that was stopped has nobody
        // to bring it back, and a service that starts is a better end than a machine cleaned off.
        _services.State = ServiceRunState.Stopped;
        _workingAfter = TimeSpan.Zero;

        var exit = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(0, _cleanCalls);
        Assert.Equal(1, _steps.Count(step => step == "start-service"));
    }

    [Fact]
    public async Task Recover_GivesTheStartWhatIsLeftOfTheWaitAndNoMoreThanThat()
    {
        // Starting the service blocks until it is running or the timeout runs out, so what is left of the wait is its budget; helping then cannot cost more than waiting.
        _services.State = ServiceRunState.Stopped;
        _workingAfter = TimeSpan.Zero;

        await RunAsync();

        // The nudge falls on the first poll, so the whole of the wait is still ahead of it.
        Assert.Equal(TwoMinutes - CleanupBudget, _services.StartTimeout);
    }

    [Fact]
    public async Task Recover_WithSomethingElseHoldingThePipeWhileTheManagerSaysStoppedClearsTheMachine()
    {
        // The two halves apart: an orphaned process from an earlier install still holds the pipe and answers while the manager reports the service stopped.
        // Something answering is not the service running.
        _services.State = ServiceRunState.Stopped;
        _serviceAnswers = true;
        _services.StartThrows = new InvalidOperationException(
            "Cannot start service ChronosService on computer '.'.", new Win32Exception(FileNotFound));

        var exit = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(1, _cleanCalls);

        // The reason it could not be started is the only diagnosis available; read as the manager's restart race it would lose the missing-executable sentence.
        Assert.Contains("could not be started", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recover_KeepsWaitingWhenTheManagerIsAlreadyStartingTheService()
    {
        // Ordinary: the service fell over, the manager restarts it five seconds later and the boot task fires inside that window.
        // ERROR_SERVICE_ALREADY_RUNNING arrives however carefully the state was read.
        _services.State = ServiceRunState.Stopped;
        _services.StartThrows = new Win32Exception(ServiceInterop.ERROR_SERVICE_ALREADY_RUNNING);
        _workingAfter = TimeSpan.FromSeconds(5);

        var exit = await RunAsync();

        // A machine that is fixing itself is not one to strip or to report a failure about.
        Assert.Equal(0, exit);
        Assert.Equal(0, _cleanCalls);
        Assert.True(_error.ToString().Length == 0, _error.ToString());
    }

    [Fact]
    public async Task Recover_KeepsWaitingWhenTheServiceCannotAcceptControlYet()
    {
        // The same race the other way round, and the shape ServiceController raises it in: a
        // Win32Exception with a sentence of the framework's own wrapped around it.
        _services.State = ServiceRunState.Stopped;
        _services.StartThrows = new InvalidOperationException(
            "Cannot start service ChronosService on computer '.'.",
            new Win32Exception(ServiceInterop.ERROR_SERVICE_CANNOT_ACCEPT_CTRL));
        _workingAfter = TimeSpan.FromSeconds(10);

        var exit = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(0, _cleanCalls);
        Assert.True(_error.ToString().Length == 0, _error.ToString());
    }

    [Fact]
    public async Task Recover_ReportsAStartThatFailedForAnyOtherReasonAndStillClearsTheMachine()
    {
        _services.State = ServiceRunState.Stopped;
        _services.StartThrows = new InvalidOperationException("Access is denied.");

        var exit = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(1, _cleanCalls);
        Assert.Contains("could not be started", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recover_WithNoServiceRegisteredDoesNotWaitForOneToAppear()
    {
        // Nothing starts a service that is not registered, so waiting would be two minutes of an unusable machine on a certainty.
        _services.State = ServiceRunState.NotInstalled;

        var exit = await RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(1, _cleanCalls);
        Assert.Equal(TimeSpan.Zero, _clock.Elapsed);
    }

    [Fact]
    public async Task Recover_KeepsWaitingWhenTheServiceManagerCannotBeAsked()
    {
        // "You may not ask" is not an answer about the service, and a machine whose manager was
        // briefly busy at boot is not a machine to strip.
        _services.QueryThrows = new InvalidOperationException("The service manager is not available.");

        await RunAsync();

        // It waited the wait out rather than deciding on the first refusal.
        Assert.True(_clock.Elapsed >= TwoMinutes - CleanupBudget, $"waited {_clock.Elapsed}");
        Assert.Equal(1, _cleanCalls);

        // Once, not once per poll: twenty-four copies of it would bury what the cleanup then says.
        Assert.Equal(1, Occurrences(_error.ToString(), "could not be asked"));

        // "You may not ask" was not filed as "stopped": a manager that cannot be asked has said nothing about the service, so there is no state to act on.
        Assert.DoesNotContain("start-service", _steps, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Recover_WritesOneErrorEntryWhenItClearsTheMachine()
    {
        _services.State = ServiceRunState.Stopped;

        await RunAsync();

        // One Error entry in the Windows event log.
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(SystemEventLevel.Error, entry.Level);
        Assert.Equal(ChronosEvents.RecoveryCleared, entry.EventId);
    }

    [Fact]
    public async Task Recover_ClearsTheStateOfASessionWithHoursLeftOnIt()
    {
        // An unfinished session is not a reason to leave the machine unusable.
        _state.Save(SessionEndingIn(TimeSpan.FromHours(3)));
        _services.State = ServiceRunState.Stopped;

        await RunAsync();

        Assert.Null(_state.Load());
        Assert.False(File.Exists(_paths.StateFile));
    }

    [Fact]
    public async Task Recover_ReportsAFailedCleanAndStillWritesToTheLog()
    {
        _services.State = ServiceRunState.Stopped;
        _cleanExit = 4;

        var exit = await RunAsync();

        Assert.Equal(4, exit);
        Assert.Equal(ChronosEvents.RecoveryFailed, Assert.Single(_log.Entries).EventId);
    }

    [Fact]
    public async Task Recover_ReportsACleanupThatCouldNotRunAtAllAndFinishesTheRest()
    {
        // A cleanup that raises rather than returns a code: the filter engine will not open or the hosts file is held.
        // Reporting success would leave the filters in force with an exit code saying it worked.
        _services.State = ServiceRunState.Stopped;
        _cleanThrows = true;

        var exit = await RunAsync();

        Assert.Equal(4, exit);
        Assert.Equal(ChronosEvents.RecoveryFailed, Assert.Single(_log.Entries).EventId);
        Assert.Contains("could not be taken off", _error.ToString(), StringComparison.Ordinal);

        // The rest of the procedure still ran: the record of the session is what would put the layers back.
        Assert.Contains("clear-state", _steps, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Recover_ClearsTheStateEvenWhenTheCleanupFailed()
    {
        // Both, always. A layer that could not be taken off is a reason to report a failure, never
        // a reason to leave the record of the session that will reapply it.
        _state.Save(SessionEndingIn(TimeSpan.FromHours(3)));
        _services.State = ServiceRunState.Stopped;
        _cleanExit = 4;

        await RunAsync();

        Assert.False(File.Exists(_paths.StateFile));
    }

    [Fact]
    public async Task Recover_ReportsAStateFileItCouldNotClear()
    {
        _services.State = ServiceRunState.Stopped;
        _clearStateFails = true;

        var exit = await RunAsync();

        // The cleanup itself worked, so a machine left with the record of a session on it is the
        // whole of what went wrong - and it is still something the exit code has to say.
        Assert.Equal(4, exit);
        Assert.Equal(ChronosEvents.RecoveryFailed, Assert.Single(_log.Entries).EventId);
        Assert.Contains("session state could not be cleared", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recover_PutsTheResolversBackInsideTheTwoMinutes()
    {
        // The DNS settings of every interface come off, and names resolve through them afterwards.
        _services.State = ServiceRunState.Stopped;
        TakenOver();
        TimeSpan? restoredAt = null;
        _dnsControl.Before = _ => restoredAt ??= _clock.Elapsed;

        var exit = await RunAsync(WithTheRealCleanup());

        Assert.Equal(0, exit);
        Assert.Equal(["9.9.9.9"], _dnsMachine.Of(9).Servers);
        Assert.True(restoredAt <= TwoMinutes, $"The resolvers went back at {restoredAt}.");
        Assert.Contains("DNS settings of 1 interfaces were put back", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("changes no DNS settings", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(ChronosEvents.RecoveryCleared, Assert.Single(_log.Entries).EventId);
    }

    [Fact]
    public async Task Recover_PutsTheResolversBackBeforeTheStateIsCleared()
    {
        // The order the steps are specified in.
        _services.State = ServiceRunState.Stopped;
        TakenOver();

        await RunAsync(WithTheRealCleanup());

        Assert.True(
            _steps.IndexOf("netsh:9") is >= 0 and var netsh && netsh < _steps.IndexOf("clear-state"),
            string.Join(", ", _steps));
    }

    [Fact]
    public async Task Recover_WithAnInterfaceThatRefused_SaysNotAllOfItCameOff()
    {
        _services.State = ServiceRunState.Stopped;
        TakenOver();
        _dnsControl.Refuse.Add(9);

        var exit = await RunAsync(WithTheRealCleanup());

        Assert.Equal(4, exit);
        Assert.Contains("could not be put back", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(ChronosEvents.RecoveryFailed, Assert.Single(_log.Entries).EventId);
    }

    [Fact]
    public async Task Recover_WhileTheServiceHoldsTheSettings_SaysNotAllOfItCameOff()
    {
        // A jammed service can still be mid-pass; its wait fits inside the cleanup's budget.
        _services.State = ServiceRunState.Stopped;
        TakenOver();
        _dnsLock.HeldElsewhere = true;

        var exit = await RunAsync(WithTheRealCleanup());

        Assert.Equal(4, exit);
        Assert.Empty(_dnsControl.Calls);
        Assert.True(Assert.Single(_dnsLock.Waits) < CleanupBudget);
        Assert.Equal(ChronosEvents.RecoveryFailed, Assert.Single(_log.Entries).EventId);
    }

    [Fact]
    public async Task Recover_WithAServiceThatAnswers_LeavesTheResolversAlone()
    {
        _services.State = ServiceRunState.Running;
        _serviceAnswers = true;
        TakenOver();

        await RunAsync(WithTheRealCleanup());

        Assert.Empty(_dnsControl.Calls);
        Assert.NotNull(_dnsBackups.Load());
    }

    private Task<int> RunAsync() => RunAsync(_setup);

    private Task<int> RunAsync(SetupContext setup) =>
        RecoverCommand.RunAsync(setup, _clock, AdvanceAsync, _output, _error, CancellationToken.None);

    /// <summary>The context with the cleanup the console gets, over a hosts file, a filter engine and interfaces that exist only in this test.</summary>
    private SetupContext WithTheRealCleanup()
    {
        var hosts = Path.Combine(_root, "hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");

        return _setup with
        {
            Clean = (output, error) => CleanCommand.CleanAsync(
                new HostsFile(hosts),
                () => new FakeWfpEngine(),
                () => new DnsRestore(_dnsBackups, _dnsControl, _dnsMachine, NullLogger<DnsRestore>.Instance),
                _dnsLock,
                output,
                error),
        };
    }

    /// <summary>One interface a dead service left on 127.0.0.1, with its backup.</summary>
    private void TakenOver()
    {
        _dnsMachine.Add("{guid-9}", 9, isDhcp: false, "127.0.0.1", "9.9.9.9");
        _dnsBackups.Save(new DnsBackup(Boot, [new InterfaceDnsState("{guid-9}", 9, "Ethernet", IsDhcp: false, ["9.9.9.9"])]));
    }

    /// <summary>The wait, where most time passes: the clock moves by exactly what was asked for.</summary>
    private Task AdvanceAsync(TimeSpan span, CancellationToken ct)
    {
        _waits.Add((_clock.Elapsed, span));
        Advance(span);

        return Task.CompletedTask;
    }

    /// <summary>Time passing and the machine afterwards. Asking a jammed service costs the probe's whole patience, so the clock moves from here too.</summary>
    private void Advance(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return;
        }

        _clock.Advance(span);

        if (_workingAfter is { } after && _clock.Elapsed >= after)
        {
            _services.State = ServiceRunState.Running;
            _serviceAnswers = true;
        }
    }

    /// <summary>The service on its pipe. A machine with nothing behind it raises the broken-pipe failure rather than answering "no"; the command reads that exception as "did not answer".</summary>
    private Task<IpcResponse> ServiceAnswersAsync(CancellationToken ct)
    {
        _probesAt.Add(_clock.Elapsed);

        var answers = _serviceAnswers;
        Advance(_probeCost);

        return answers
            ? Task.FromResult(new IpcResponse { Accepted = true })
            : Task.FromException<IpcResponse>(new IOException("The pipe is broken."));
    }

    private Task<CleanResult> CleanAsync(TextWriter output, TextWriter error)
    {
        _cleanCalls++;
        _steps.Add("clean");

        if (_cleanThrows)
        {
            // Its filter engine may be the broken part, and a cleanup that cannot open it raises rather than returns.
            throw new InvalidOperationException("The filter engine could not be opened.");
        }

        output.WriteLine("The hosts block was removed.");

        return Task.FromResult(new CleanResult(_cleanExit, RemovedSomething: true));
    }

    private void ClearState()
    {
        _steps.Add("clear-state");

        if (_clearStateFails)
        {
            throw new IOException("The file is in use by another process.");
        }

        _state.Clear();
    }

    private static BlockSession SessionEndingIn(TimeSpan left) => new()
    {
        Id = Guid.NewGuid(),
        StartedAt = Boot,
        EndsAt = Boot + left,
        CoolDown = TimeSpan.FromMinutes(5),
        Rules = BlockList.Empty.WithSite(new SiteRule("example.com", includeSubdomains: true)),
    };

    private static int Occurrences(string text, string needle)
    {
        var found = 0;

        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }
}
