using Chronos.Core.Rules;
using Chronos.Ipc;
using Chronos.Service.Apps;
using Chronos.Service.Ipc;
using Chronos.Service.Rules;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Tests;

public sealed class AppWatchdogTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeProcessControl _processes = new();
    private readonly ServiceTestClock _clock = new(Start);
    private readonly CapturingLogger<AppWatchdog> _logger = new();
    private readonly EventBus _events = new();

    private AppWatchdog Create() =>
        new(_processes, new WindowsProtectedAppPolicy(), _clock, _events, TestStatus.Blank, _logger);

    private static AppRuleIndex Index(params string[] fileNames) =>
        AppRuleIndex.Build([.. fileNames.Select(name => new AppRule(AppMatchKind.FileName, name))]);

    [Fact]
    public void OnProcessStarted_TerminatesAMatchingProcess()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\games\game.exe"));

        Assert.Equal([4242], _processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_LeavesAProcessThatMatchesNothing()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\work\editor.exe"));

        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_DoesNothingWhileDisarmed()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));
        watchdog.Disarm();

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\games\game.exe"));

        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_RefusesAProtectedProcessEvenWhenARuleNamesIt()
    {
        // The config is a file and can be edited around the UI, so the guard sits immediately before the kill.
        var watchdog = Create();
        watchdog.Arm(Index("explorer.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"C:\Windows\explorer.exe"));

        Assert.Empty(_processes.Killed);
        Assert.Contains(_logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public void OnProcessStarted_RefusesAProcessInsideAWindowsSystemDirectory()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var watchdog = Create();
        watchdog.Arm(Index("anything.exe"));

        var path = Path.Combine(system, "anything.exe");
        watchdog.OnProcessStarted(new ProcessStarted(4242, path));

        Assert.Empty(_processes.Killed);
        // The protection policy builds its reason around the full path, so a path most easily escapes into a record above Debug here.
        Assert.DoesNotContain(
            _logger.Entries,
            entry => entry.Level >= LogLevel.Information
                && entry.Message.Contains(path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnProcessStarted_TreatsAProcessThatEndedOnItsOwnAsNormal()
    {
        _processes.AlreadyGone.Add(4242);
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\games\game.exe"));

        Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public void OnProcessStarted_ReportsAtWarningWhenTheProcessSurvives()
    {
        // A process that refuses to die is the opposite of one that already exited: the block did not happen, and the user must be able to find out.
        _processes.Denied.Add(4242);
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\games\game.exe"));

        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void OnProcessStarted_TerminatesOnlyTheMatchedProcess()
    {
        // No parent-tree walk: Explorer is the parent of most applications.
        _processes.Running.Add(new RunningProcess(1000, @"C:\Windows\explorer.exe"));
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\games\game.exe"));

        Assert.Equal([4242], _processes.Killed);
    }

    [Fact]
    public void SweepExisting_TerminatesApplicationsThatWereAlreadyRunning()
    {
        // Both scenarios: a session starting while the application is open, and the service starting after a reboot.
        _processes.Running.Add(new RunningProcess(1000, @"D:\games\game.exe"));
        _processes.Running.Add(new RunningProcess(1001, @"D:\work\editor.exe"));
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        var killed = watchdog.SweepExisting();

        Assert.Equal(1, killed);
        Assert.Equal([1000], _processes.Killed);
    }

    [Fact]
    public void SweepExisting_DoesNotTouchProtectedProcesses()
    {
        _processes.Running.Add(new RunningProcess(1000, @"C:\Windows\explorer.exe"));
        var watchdog = Create();
        watchdog.Arm(Index("explorer.exe"));

        Assert.Equal(0, watchdog.SweepExisting());
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void SweepExisting_DoesNothingWhileDisarmed()
    {
        _processes.Running.Add(new RunningProcess(1000, @"D:\games\game.exe"));
        var watchdog = Create();

        Assert.Equal(0, watchdog.SweepExisting());
        Assert.Equal(0, _processes.ListCalls);
    }

    [Fact]
    public void ARestartingApplicationIsKilledEveryTimeButLoggedOnce()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        for (var i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            watchdog.OnProcessStarted(new ProcessStarted(4000 + i, @"D:\games\game.exe"));
        }

        Assert.Equal(20, _processes.Killed.Count);
        Assert.Single(TerminationRecords());
    }

    [Fact]
    public void TheAggregatedRecordCarriesTheSuppressedCount()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        for (var i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            watchdog.OnProcessStarted(new ProcessStarted(4000 + i, @"D:\games\game.exe"));
        }

        _clock.Advance(TimeSpan.FromMinutes(6));
        watchdog.OnProcessStarted(new ProcessStarted(4999, @"D:\games\game.exe"));

        var records = TerminationRecords();
        Assert.Equal(2, records.Count);
        Assert.Contains("20", records[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalIsAggregatedLikeATermination()
    {
        // A file-name rule such as RuntimeBroker.exe passes the check made at session start (no directory in
        // the rule value), then fires on every start of a process Windows launches constantly.
        // Unthrottled, it would roll every useful record out of a 10 MB log within hours.
        var watchdog = Create();
        watchdog.Arm(Index("explorer.exe"));

        for (var i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            watchdog.OnProcessStarted(new ProcessStarted(4000 + i, @"C:\Windows\explorer.exe"));
        }

        Assert.Empty(_processes.Killed);
        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public void ADenialIsAggregatedLikeATermination()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        for (var i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            _processes.Denied.Add(4000 + i);
            watchdog.OnProcessStarted(new ProcessStarted(4000 + i, @"D:\games\game.exe"));
        }

        Assert.Equal(20, _processes.Killed.Count);
        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void ATerminationAndADenialOfTheSameApplicationAreRecordedApart()
    {
        // The outcome is part of the throttle key: a denial must not be swallowed by an earlier termination.
        _processes.Denied.Add(4243);
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\games\game.exe"));
        watchdog.OnProcessStarted(new ProcessStarted(4243, @"D:\games\game.exe"));

        Assert.Single(TerminationRecords());
        Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void Disarm_ReportsTheTerminationsThatWereStillSuppressed()
    {
        // A burst that ends with the session would otherwise be one record with no counter, since only a later kill emits the count.
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        for (var i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            watchdog.OnProcessStarted(new ProcessStarted(4000 + i, @"D:\games\game.exe"));
        }

        watchdog.Disarm();

        var records = TerminationRecords();
        Assert.Equal(2, records.Count);
        Assert.Contains("19", records[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnProcessStarted_ResolvesABareNameBeforeTheProtectionCheck()
    {
        // The fallback event source reports a bare executable name, which cannot be tested against the
        // protected directories. This keeps a kill storm off Windows components.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _processes.Paths[4242] = Path.Combine(windows, "SystemApps", "shell.exe");
        var watchdog = Create();
        watchdog.Arm(Index("shell.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, "shell.exe"));

        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_ChecksWhatItHasWhenThePathCannotBeResolved()
    {
        // Resolution can fail for a process that already exited or one this account cannot open.
        // The check still runs on the name.
        var watchdog = Create();
        watchdog.Arm(Index("explorer.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, "explorer.exe"));

        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_TerminatesABareNameThatIsNotProtected()
    {
        _processes.Paths[4242] = @"D:\games\game.exe";
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, "game.exe"));

        Assert.Equal([4242], _processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_TerminatesAProcessMatchedOnlyByFullPath()
    {
        // A process-start event carries only a bare file name; a full-path rule must still catch it by
        // resolving the path through the operating system.
        const string fullPath = @"C:\Users\test\AppData\Local\Temp\chronos-victim.exe";
        _processes.Paths[4242] = fullPath;
        var watchdog = Create();
        watchdog.Arm(AppRuleIndex.Build([new AppRule(AppMatchKind.FullPath, fullPath)]));

        watchdog.OnProcessStarted(new ProcessStarted(4242, "chronos-victim.exe"));

        Assert.Equal([4242], _processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_DoesNotResolveThePathWhenNoFullPathRuleIsArmed()
    {
        // With a file-name-only rule set, a miss must not spend a system call resolving a path nothing can use.
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\work\editor.exe"));

        Assert.Equal(0, _processes.ImagePathOfCalls);
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_DoesNothingWhenTheFullPathCannotBeResolved()
    {
        // The process ended between the event and the lookup: expected, not an error.
        var watchdog = Create();
        watchdog.Arm(AppRuleIndex.Build([new AppRule(AppMatchKind.FullPath, @"D:\games\game.exe")]));

        var exception = Record.Exception(
            () => watchdog.OnProcessStarted(new ProcessStarted(4242, "game.exe")));

        Assert.Null(exception);
        Assert.Empty(_processes.Killed);
        // A full-path rule must not go quiet just because an exited process looks like a denied one.
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Debug);
        Assert.DoesNotContain(
            _logger.Entries,
            entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public void OnProcessStarted_LeavesAProcessMatchedByFullPathOnlyInAnotherDirectory()
    {
        // Bare-name event, full-path rule armed, resolved path in a different directory than the rule names:
        // the same file name elsewhere is a different application.
        _processes.Paths[4242] = @"D:\work\game.exe";
        var watchdog = Create();
        watchdog.Arm(AppRuleIndex.Build([new AppRule(AppMatchKind.FullPath, @"D:\games\game.exe")]));

        watchdog.OnProcessStarted(new ProcessStarted(4242, "game.exe"));

        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_RefusesAProtectedProcessMatchedOnlyByFullPath()
    {
        // A full-path rule that only matches after resolution must still pass the protected-directory
        // check, not just the protected-name one.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(windows, "SystemApps", "shell.exe");
        _processes.Paths[4242] = path;
        var watchdog = Create();
        watchdog.Arm(AppRuleIndex.Build([new AppRule(AppMatchKind.FullPath, path)]));

        watchdog.OnProcessStarted(new ProcessStarted(4242, "shell.exe"));

        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void OnProcessStarted_ResolvesThePathOnlyOnceWhenAFullPathRuleMatches()
    {
        // The protection check must reuse the path already resolved, not resolve it again for the same event.
        const string fullPath = @"D:\games\game.exe";
        _processes.Paths[4242] = fullPath;
        var watchdog = Create();
        watchdog.Arm(AppRuleIndex.Build([new AppRule(AppMatchKind.FullPath, fullPath)]));

        watchdog.OnProcessStarted(new ProcessStarted(4242, "game.exe"));

        Assert.Equal(1, _processes.ImagePathOfCalls);
    }

    [Fact]
    public void TwoDifferentApplicationsAreThrottledSeparately()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe", "chat.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(1, @"D:\games\game.exe"));
        watchdog.OnProcessStarted(new ProcessStarted(2, @"D:\chat\chat.exe"));

        Assert.Equal(2, TerminationRecords().Count);
    }

    [Fact]
    public void Arm_ReplacesTheRuleSetRatherThanAddingToIt()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));
        watchdog.Arm(Index("chat.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(1, @"D:\games\game.exe"));
        watchdog.OnProcessStarted(new ProcessStarted(2, @"D:\chat\chat.exe"));

        Assert.Equal([2], _processes.Killed);
    }

    [Fact]
    public void NoImagePathReachesTheLogAboveDebug()
    {
        // As with domains, the log must not become a record of what the user tried to open.
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\private\game.exe"));

        Assert.DoesNotContain(
            _logger.Entries,
            entry => entry.Level >= LogLevel.Information
                && entry.Message.Contains(@"D:\private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnProcessStarted_PublishesTheBlockWithTheStatusTheScreenNeeds()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));
        using var subscription = _events.Subscribe(out var published);

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\games\game.exe"));

        Assert.True(published.TryRead(out var one));
        Assert.Equal(IpcEventKind.AppBlocked, one!.Kind);
        Assert.Equal("game.exe", one.AppName);

        // The status travels with the block, so the screen can show the time remaining without asking.
        Assert.NotNull(one.Status);
    }

    [Fact]
    public void OnProcessStarted_PublishesTheFileNameAndNotThePath()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));
        using var subscription = _events.Subscribe(out var published);

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\private\games\game.exe"));

        Assert.True(published.TryRead(out var one));
        Assert.Equal("game.exe", one!.AppName);
    }

    [Fact]
    public void OnProcessStarted_PublishesNothingWhenTheProcessWasNotTerminated()
    {
        var watchdog = Create();
        watchdog.Arm(Index("explorer.exe"));
        using var subscription = _events.Subscribe(out var published);

        // Protected: refused rather than terminated, and there is no block screen for a running process.
        watchdog.OnProcessStarted(new ProcessStarted(4242, @"C:\Windows\explorer.exe"));

        Assert.False(published.TryRead(out _));
    }

    [Fact]
    public void OnProcessStarted_PublishesEveryTerminationRatherThanEveryRecord()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));
        using var subscription = _events.Subscribe(out var published);

        for (var processId = 1; processId <= 5; processId++)
        {
            watchdog.OnProcessStarted(new ProcessStarted(processId, @"D:\games\game.exe"));
        }

        // The log throttles a restarting application to one record; the block screen is rate-limited by
        // the interface, so events must not be suppressed here.
        var delivered = 0;
        while (published.TryRead(out _))
        {
            delivered++;
        }

        Assert.Equal(5, delivered);
    }

    [Fact]
    public void OnProcessStarted_RecordsThatNoInterfaceIsAttachedExactlyOnce()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        for (var processId = 1; processId <= 20; processId++)
        {
            watchdog.OnProcessStarted(new ProcessStarted(processId, @"D:\games\game.exe"));
        }

        // The process ends anyway and the log says the interface was not there; a self-restarting
        // application must not turn that into twenty lines.
        Assert.Equal(20, _processes.Killed.Count);
        Assert.Single(NoInterfaceRecords());
    }

    [Fact]
    public void OnProcessStarted_KeepsTheApplicationOutOfTheRecordAboutTheMissingInterface()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\games\game.exe"));

        // The record is about the interface being absent, not about what was launched; the name goes on the wire, not in the log.
        var record = Assert.Single(NoInterfaceRecords());
        Assert.DoesNotContain("game", record.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnProcessStarted_WritesNoSuchRecordWhileAnInterfaceIsAttached()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));
        using var subscription = _events.Subscribe(out _);

        watchdog.OnProcessStarted(new ProcessStarted(4242, @"D:\games\game.exe"));

        Assert.Empty(NoInterfaceRecords());
    }

    [Fact]
    public void OnProcessStarted_RecordsTheMissingInterfaceAgainAfterOneHasComeAndGone()
    {
        var watchdog = Create();
        watchdog.Arm(Index("game.exe"));

        watchdog.OnProcessStarted(new ProcessStarted(1, @"D:\games\game.exe"));

        var subscription = _events.Subscribe(out _);
        watchdog.OnProcessStarted(new ProcessStarted(2, @"D:\games\game.exe"));
        subscription.Dispose();

        watchdog.OnProcessStarted(new ProcessStarted(3, @"D:\games\game.exe"));

        // Suppressing repeats is not saying it once and never again: the interface having been there in
        // between makes its absence news a second time.
        Assert.Equal(2, NoInterfaceRecords().Count);
    }

    /// <summary>Application termination records, excluding the missing-interface one.</summary>
    private IReadOnlyList<(LogLevel Level, string Message)> TerminationRecords() =>
    [
        .. _logger.Entries.Where(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("Terminated", StringComparison.Ordinal)),
    ];

    private IReadOnlyList<(LogLevel Level, string Message)> NoInterfaceRecords() =>
    [
        .. _logger.Entries.Where(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("interface", StringComparison.OrdinalIgnoreCase)),
    ];
}
