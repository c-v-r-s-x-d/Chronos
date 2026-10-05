using System.Runtime.Versioning;
using Chronos.Cli.Commands;
using Chronos.Service.Configuration;
using Chronos.Service.Diagnostics;
using Chronos.Service.Dns;
using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>
/// Both halves of putting the product on a machine and taking it back off, against fakes, so the
/// order of the steps can be read back without registering a service, writing an event, creating a
/// task or touching %ProgramData%. The one real thing is a directory under the temporary path, for
/// files that must survive an uninstall and files that must not survive a purge.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallCommandTests : IDisposable
{
    private const string InstallDirectory = @"C:\Program Files\Chronos";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-setup-" + Guid.NewGuid().ToString("N"));

    /// <summary>Every step that touched the machine, in the order it was taken.</summary>
    private readonly List<string> _steps = [];

    private readonly FakeServiceRegistration _services;
    private readonly FakeTaskRegistration _tasks;
    private readonly FakeAutostartEntry _autostart;
    private readonly FakeSystemEventLog _log;
    private readonly ChronosPaths _paths;
    private readonly SetupContext _setup;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    private CleanResult _clean = new(0, RemovedSomething: false);
    private Exception? _cleanThrows;
    private Exception? _dataDirectoryThrows;
    private Exception? _clearStateThrows;
    private int _stateCleared;

    /// <summary>What the DNS restore run again after the stop finds and says.</summary>
    private CleanResult _dnsAgain = new(0, RemovedSomething: false);

    private string _dnsAgainSays = "There was no DNS backup, so there were no DNS settings to put back.";

    /// <summary>What the cleanup and the second restore write on the error writer.</summary>
    private string? _cleanComplains;

    /// <summary>A line of its own the cleanup writes on the output writer.</summary>
    private string? _cleanSays;

    private string? _dnsAgainComplains;

    /// <summary>What becomes of HKLM\SOFTWARE\Chronos.</summary>
    private ProductKeyRemoval _productKey = ProductKeyRemoval.NotThere;

    private Exception? _productKeyThrows;

    public InstallCommandTests()
    {
        _services = new FakeServiceRegistration(_steps);
        _tasks = new FakeTaskRegistration(_steps);
        _autostart = new FakeAutostartEntry(_steps);
        _log = new FakeSystemEventLog { Steps = _steps, SourceIsRegistered = false };
        _paths = new ChronosPaths(Path.Combine(_root, "data"), Path.Combine(_root, "user"));

        // The machine an uninstallation runs on: the product has been used, so there is a configuration file in the data directory.
        Directory.CreateDirectory(_paths.DataDirectory);
        File.WriteAllText(_paths.ConfigFile, "{}");

        _setup = new SetupContext(
            _services,

            // Neither command asks the service anything down the pipe, and a fake that answered would hide it if one started to: this throws.
            _ => throw new InvalidOperationException("Installation does not ask the service anything."),
            _tasks,
            _log,
            _autostart,
            _paths,
            ServiceDefinition.Chronos(Path.Combine(InstallDirectory, "Chronos.Service.exe")),
            Path.Combine(InstallDirectory, "chronos.exe"),
            CleanAsync,
            ClearState,
            CreateDataDirectory,

            // The sources of the diagnostic package, which neither command reads.
            () => throw new InvalidOperationException("Installation does not list the filters."),
            () => throw new InvalidOperationException("Installation does not read the hosts block."),
            _ => throw new InvalidOperationException("Installation does not look at port 53."),
            () => throw new InvalidOperationException("Installation does not read the DNS settings."),
            _ => throw new InvalidOperationException("Installation does not try port 53."),
            () => throw new InvalidOperationException("Installation reads the DNS backup through the cleanup alone."),
            RestoreDnsAgain,
            RemoveProductKey);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Install_RegistersTheServiceTheTaskAndTheEventSource()
    {
        var exit = await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(0, exit);
        Assert.Equal("ChronosService", _services.Installed?.Name);
        Assert.True(_tasks.Registered);
        Assert.Equal(1, _log.EnsureSourceCalls);
        Assert.Equal(ServiceRunState.Running, _services.State);
        Assert.True(Directory.Exists(_paths.DataDirectory));
    }

    [Fact]
    public async Task Install_RegistersTheEventSourceBeforeTheServiceIsStarted()
    {
        // ServiceLogging asks once, while being configured, whether the source exists and attaches its event
        // log sink only if it does. A source registered after the service started leaves the running
        // service unable to duplicate its Error entries until something restarts it.
        await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(
            ["clean", "data-directory", "event-source", "install-service", "register-task", "start-service"],
            _steps);
    }

    [Fact]
    public async Task Install_BuildsTheRecoveryTaskFromThePathToTheCommand()
    {
        await InstallCommand.RunAsync(_setup, _output, _error);

        // The document, not a name and a path assembled here: the task is a pure function of the path to chronos.exe.
        Assert.Equal(RecoveryTaskDefinition.BuildXml(Path.Combine(InstallDirectory, "chronos.exe")), _tasks.Xml);
    }

    [Theory]
    [InlineData(ServiceRunState.Stopped)]
    [InlineData(ServiceRunState.Starting)]
    [InlineData(ServiceRunState.Running)]
    [InlineData(ServiceRunState.Stopping)]
    public async Task Install_OnAMachineThatAlreadyHasItBringsTheServiceInLineAndSucceeds(ServiceRunState state)
    {
        // Every state but NotInstalled. Installing twice means the same as installing once: install from a
        // build, then run the MSI. `chronos install` is a deferred custom action with Return="check", where
        // any non-zero answer is error 1722 and Windows rolls the installation back, so a refusal there
        // would leave a machine that already had a service with no product at all.
        _services.State = state;

        var exit = await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(0, exit);
        Assert.Equal(_setup.Definition, _services.Installed);
        Assert.Contains("already registered", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _error.ToString());
    }

    [Fact]
    public async Task Install_OverAnExistingServiceUpdatesItRatherThanRemovingIt()
    {
        // Removing and recreating would take a machine holding somebody to a session through a window with
        // no service, and the cleanup that opens a fresh installation would take the block off. Instead the
        // service is brought in line where it stands, with the whole definition this build asks for,
        // including the failure actions and the flag that decides whether they ever run.
        _services.State = ServiceRunState.Running;

        await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(
            ["data-directory", "event-source", "update-service", "register-task", "start-service"],
            _steps);
        Assert.DoesNotContain("remove-service", _steps);
        Assert.DoesNotContain("clean", _steps);
    }

    [Fact]
    public async Task Install_OverAServiceItCouldNotBringInLineIsAFailedInstallation()
    {
        // The one thing the idempotent path must not become: a run that could not make the machine match
        // the definition and answered 0 anyway.
        _services.State = ServiceRunState.Running;
        _services.UpdateThrows = new UnauthorizedAccessException("Access is denied.");

        var exit = await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(4, exit);
        Assert.Contains("could not be brought in line", _error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("start-service", _steps);
    }

    [Fact]
    public async Task Install_WillNotInstallOverAServiceManagerThatCouldNotBeAsked()
    {
        // Query answers "not installed" for the one failure that is an answer and throws for the rest.
        // Reading a refusal as an empty machine is how a working installation gets a second service on top.
        _services.QueryThrows = new InvalidOperationException("Access is denied.");

        var exit = await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(4, exit);
        Assert.Empty(_steps);
        Assert.Contains("could not be asked", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_TakesOffWhatAnEarlierBuildLeftBeforeItRegistersAnything()
    {
        // A machine upgraded from an older build carries a persistent provider and sub-layer that this build neither creates nor deletes, so nothing else removes them.
        _clean = new CleanResult(0, RemovedSomething: true);

        var exit = await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(0, exit);
        Assert.Equal("clean", _steps[0]);
        Assert.Contains("The Chronos filter provider was removed.", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_SaysNothingAboutACleanupThatFoundNothing()
    {
        // The ordinary machine: three sentences about objects that were never there would stand in front of every installation.
        await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.DoesNotContain("earlier installation", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("filter provider", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_GoesOnWhenTheCleanupFailedAndAnswersThatItIsInstalled()
    {
        // Leftovers from an older build are not a reason to refuse to put the working one on, nor, in the
        // MSI, to answer a deferred custom action with the code that means "roll back". An installation
        // that worked answers 0 and says the rest in words.
        _clean = new CleanResult(4, RemovedSomething: false);

        var exit = await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(0, exit);
        Assert.Equal(ServiceRunState.Running, _services.State);
        Assert.Contains("Chronos is installed.", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("could not be taken off", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_SaysInTheEventLogThatTheCleanupIsTheOnePartThatDidNotWork()
    {
        // The MSI runs this with no console, so the entry is all an administrator gets. Warning, since
        // something on this machine still needs looking at; an entry, since the installation did happen.
        _clean = new CleanResult(4, RemovedSomething: false);

        await InstallCommand.RunAsync(_setup, _output, _error);

        var entry = Assert.Single(_log.Entries);

        Assert.Equal(ChronosEvents.Installed, entry.EventId);
        Assert.Equal(SystemEventLevel.Warning, entry.Level);
    }

    [Theory]
    [InlineData("data-directory")]
    [InlineData("event-source")]
    [InlineData("install-service")]
    [InlineData("register-task")]
    [InlineData("start-service")]
    public async Task Install_StopsAtWhicheverStepFailed(string step)
    {
        // Each step is something the ones after it need, the middle of the list as well as its ends.
        // Carrying on past a service that could not be registered would register a boot-time task for it
        // and start a service that is not there.
        string[] all = ["clean", "data-directory", "event-source", "install-service", "register-task", "start-service"];
        Arm(step, new UnauthorizedAccessException("Access is denied."));

        var exit = await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(4, exit);
        Assert.Equal(all[..(Array.IndexOf(all, step) + 1)], _steps);
        Assert.Contains("was not fully installed", _error.ToString(), StringComparison.Ordinal);

        // Nothing in the log either: the entry says the product is on this machine, and it is not.
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Install_GivesTheServiceLongEnoughToStart()
    {
        // A service reads its configuration and opens the filter engine before it reports itself running.
        // A timeout of a second or two would fail every real installation on a busy machine.
        await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(InstallCommand.StartTimeout, _services.StartTimeout);
        Assert.True(
            InstallCommand.StartTimeout >= TimeSpan.FromSeconds(30),
            $"A start timeout of {InstallCommand.StartTimeout} is shorter than a service takes to start.");
    }

    [Fact]
    public async Task Install_SaysInTheEventLogThatItRan()
    {
        // The installer runs this with no console, so the event log is the only place an administrator can see that it ran.
        await InstallCommand.RunAsync(_setup, _output, _error);

        var entry = Assert.Single(_log.Entries);

        Assert.Equal(ChronosEvents.Installed, entry.EventId);
        Assert.Equal(SystemEventLevel.Information, entry.Level);
        Assert.False(_log.WroteWithoutSource);
    }

    [Fact]
    public async Task Install_SaysNothingInTheEventLogWhenItDidNotFinish()
    {
        _services.StartThrows = new InvalidOperationException("The service did not start.");

        var exit = await InstallCommand.RunAsync(_setup, _output, _error);

        Assert.Equal(4, exit);
        Assert.Empty(_log.Entries);
        Assert.Contains("could not be started", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_TakesTheBlockOffBeforeStoppingTheService()
    {
        // The other order leaves a window in which the service's next pass puts the filters back, and
        // then the service is gone and nothing will remove them.
        _services.State = ServiceRunState.Running;
        _tasks.Registered = true;
        _autostart.Enabled = true;

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(
            ["clean", "stop", "remove-service", "restore-dns", "remove-product-key", "clear-state", "remove-task", "remove-autostart", "remove-source"],
            _steps);
    }

    [Fact]
    public async Task Uninstall_PutsTheResolversBackAgainWhenTheServiceHadTakenThemBeforeItStopped()
    {
        // The service's next pass can re-take them between the cleanup and the stop.
        _services.State = ServiceRunState.Running;
        _dnsAgain = new CleanResult(0, RemovedSomething: true);
        _dnsAgainSays = "The DNS settings of 2 interfaces were put back.";

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(0, exit);
        Assert.Contains("Put back once more after the service stopped:", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("had taken the interfaces again", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("  The DNS settings of 2 interfaces were put back.", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_SaysOfAnAbsentAdapterOnlyThatTheManualFallbackPutsItBack()
    {
        // The first clean's "for when they return" is not true once the product is gone.
        _cleanSays = $"1 interfaces in the DNS backup are no longer on this machine; {CleanCommand.AbsentStays}";
        _clean = new CleanResult(0, RemovedSomething: true);
        _dnsAgainComplains = "1 interfaces in the DNS backup are no longer on this machine, and nothing will put ... 'Manual fallback'.";

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.DoesNotContain(CleanCommand.AbsentStays, _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("The Chronos filter provider was removed.", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(_error.ToString(), "Manual fallback"));
    }

    [Fact]
    public async Task Uninstall_SaysNothingMoreWhenTheSecondRestoreFoundNothing()
    {
        _services.State = ServiceRunState.Running;

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.DoesNotContain("had taken the interfaces again", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("There was no DNS backup", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_ASecondRestoreThatRefusedIsAFailedRun()
    {
        _services.State = ServiceRunState.Running;
        _dnsAgain = new CleanResult(4, RemovedSomething: true);

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(4, exit);
    }

    [Fact]
    public async Task Uninstall_DoesNotSayTheSameComplaintTwice()
    {
        // Not elevated, say: both restores fail the same way, and one line says it.
        const string complaint = "The DNS settings could not be put back: denied.";
        _cleanComplains = complaint;
        _dnsAgainComplains = complaint;

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(1, Occurrences(_error.ToString(), complaint));
    }

    [Fact]
    public async Task Uninstall_SaysWhatOnlyTheSecondRestoreFoundWrong()
    {
        _cleanComplains = "The hosts file could not be changed.";
        _dnsAgainComplains = "The DNS settings of 1 interfaces could not be put back.";

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Contains(_dnsAgainComplains, _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(_error.ToString(), _cleanComplains));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uninstall_RemovesTheEmptyProductKeyWithOrWithoutPurge(bool purge)
    {
        // The service created it, and nothing else would take it off.
        _productKey = ProductKeyRemoval.Removed;

        var exit = await UninstallCommand.RunAsync(_setup, purge, _output, _error);

        Assert.Equal(0, exit);
        Assert.Contains("remove-product-key", _steps);
        Assert.Contains(@"HKLM\SOFTWARE\Chronos was removed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_RemovesTheProductKeyOnlyAfterTheSecondRestore()
    {
        // A copy the second restore could not clear keeps the key, which is what it should do.
        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(_steps.IndexOf("restore-dns") + 1, _steps.IndexOf("remove-product-key"));
    }

    [Fact]
    public async Task Uninstall_SaysWhyAProductKeyThatStillHoldsSomethingIsKept()
    {
        _productKey = ProductKeyRemoval.KeptNotEmpty;

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(0, exit);
        Assert.Contains(@"HKLM\SOFTWARE\Chronos was kept", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_SaysNothingOfAProductKeyThatWasNotThere()
    {
        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.DoesNotContain(@"SOFTWARE\Chronos", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_AProductKeyThatCouldNotBeRemovedIsAFailedRun()
    {
        _productKeyThrows = new UnauthorizedAccessException("denied");

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(4, exit);
        Assert.Contains(@"HKLM\SOFTWARE\Chronos could not be removed: denied", _error.ToString(), StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string needle) =>
        text.Split(needle).Length - 1;

    [Fact]
    public async Task Uninstall_GivesTheServiceLongEnoughToStop()
    {
        // A service in the middle of taking a block off is doing what this command wants it to finish. A
        // short timeout would call that a failure and remove a service still holding the filters.
        _services.State = ServiceRunState.Running;

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(UninstallCommand.StopTimeout, _services.StopTimeout);
        Assert.True(
            UninstallCommand.StopTimeout >= TimeSpan.FromSeconds(30),
            $"A stop timeout of {UninstallCommand.StopTimeout} is shorter than a stop takes.");
    }

    [Fact]
    public async Task Uninstall_KeepsGoingAfterAStepFails()
    {
        // A run that stopped at the first failure would leave a machine the product is gone from and its changes are not.
        _services.State = ServiceRunState.Running;
        _tasks.Registered = true;
        _tasks.RemoveThrows = new InvalidOperationException("The task could not be deleted.");

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(4, exit);
        Assert.Equal(1, _log.RemoveSourceCalls);   // the step after the one that failed still ran
        Assert.Contains("task", _error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uninstall_KeepsGoingWhenTheCleanupItselfThrew()
    {
        // The cleanup reports its own failures and answers 4, but it reaches the hosts file and the filter
        // engine, and either can fail unexpectedly. The rest of the removal must still happen.
        _cleanThrows = new UnauthorizedAccessException("The hosts file is held open.");
        _tasks.Registered = true;

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(4, exit);
        Assert.False(_tasks.Registered);
        Assert.Equal(1, _log.RemoveSourceCalls);
    }

    [Fact]
    public async Task Uninstall_RemovesTheServiceEvenWhenItCouldNotBeStopped()
    {
        // A service marked for deletion goes when its process exits, a better end state than one neither stopped nor marked.
        _services.State = ServiceRunState.Running;
        _services.StopThrows = new InvalidOperationException("The service did not stop in time.");

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(4, exit);
        Assert.Contains("remove-service", _steps);
        Assert.Equal(ServiceRunState.NotInstalled, _services.State);
    }

    [Fact]
    public async Task Uninstall_WillNotReportATaskItCouldNotAskAboutAsGone()
    {
        // Exists throws for "I may not look", never for "there is none". Reading the first as the second
        // reports a boot-time task removed while it is still there.
        _tasks.ExistsThrows = new InvalidOperationException("Access is denied.");
        _tasks.Registered = true;

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(4, exit);
        Assert.DoesNotContain("remove-task", _steps);
        Assert.True(_tasks.Registered);
        Assert.DoesNotContain("recovery task was removed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_OnAMachineWhoseServiceHasAlreadyGoneTakesTheRestOff()
    {
        // The half-removed machine: no service, and a task, a log source and filters nothing else will
        // ever remove. Refusing here would leave all of it behind.
        _tasks.Registered = true;

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(0, exit);
        Assert.False(_tasks.Registered);
        Assert.Equal(1, _log.RemoveSourceCalls);
        Assert.Contains("was not registered", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_TakesTheAutostartEntryOffAndSaysWhoseItCannotReach()
    {
        _autostart.Enabled = true;

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.False(_autostart.Enabled);
        Assert.Contains("Other users", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_WarnsAboutTheOtherAccountsEvenWhenThisOneHadNoEntry()
    {
        // An administrator who removes the product without ever having run the interface is the one a guard
        // on this sentence would hide it from, and the one who has to delete those entries.
        _autostart.Enabled = false;

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Contains("Other users", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_SaysTheTrueThingAboutTheOtherAccountsEntries()
    {
        // Nothing else removes them: Autostart.Ensure only creates an entry, Disable is called nowhere but
        // here, and the executable those entries name is gone, so those interfaces never start again.
        _autostart.Enabled = true;

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        var said = _output.ToString();

        Assert.Contains("has to be deleted by hand", said, StringComparison.Ordinal);
        Assert.DoesNotContain("until they next log in", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uninstall_AnAutostartEntryThatCouldNotBeRemovedIsAFailedRun()
    {
        // The step survives its own failure, but must not report one as a success: answering 0 and
        // printing "Chronos was removed." would leave an entry pointing at a product that is gone.
        _autostart.Enabled = true;
        _autostart.DisableThrows = new UnauthorizedAccessException("The Run key could not be opened.");

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(4, exit);
        Assert.Contains("not all of it", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("autostart entry could not be removed", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_ClearsTheSavedSessionEvenWhenTheDataDirectoryStays()
    {
        // The configuration and the logs are kept. A running session's state is neither: left behind, the
        // next installation's service restores the session from it, and someone who removed the product to
        // end a session has it back, with unlock still on its cool-down.
        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(0, exit);
        Assert.Equal(1, _stateCleared);
        Assert.True(File.Exists(_paths.ConfigFile));
    }

    [Fact]
    public async Task Uninstall_ClearsTheSavedSessionOnlyOnceTheServiceCanNoLongerWriteIt()
    {
        // Before the service is gone, clearing the file is a race the service wins on its next save.
        _services.State = ServiceRunState.Running;

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.True(_steps.IndexOf("remove-service") < _steps.IndexOf("clear-state"));
    }

    [Fact]
    public async Task Uninstall_ASavedSessionThatCouldNotBeClearedIsAFailedRun()
    {
        _clearStateThrows = new IOException("state.json is held open.");

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.Equal(4, exit);
        Assert.Contains("saved session could not be cleared", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_SaysInTheEventLogThatItRanWhileThereIsStillSomewhereToSayIt()
    {
        _log.SourceIsRegistered = true;

        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        var entry = Assert.Single(_log.Entries);

        Assert.Equal(ChronosEvents.Uninstalled, entry.EventId);
        Assert.Equal(SystemEventLevel.Information, entry.Level);
        Assert.False(_log.WroteWithoutSource);
    }

    [Fact]
    public async Task Uninstall_SaysInTheEventLogWhatItCouldNotRemove()
    {
        // The failed run is the one an administrator needs this entry for, and the MSI runs the command
        // with no console. Warning rather than Information, since the two runs are not the same run.
        _log.SourceIsRegistered = true;
        _tasks.Registered = true;
        _tasks.RemoveThrows = new InvalidOperationException("The task could not be deleted.");

        var exit = await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        var entry = Assert.Single(_log.Entries);

        Assert.Equal(4, exit);
        Assert.Equal(ChronosEvents.Uninstalled, entry.EventId);
        Assert.Equal(SystemEventLevel.Warning, entry.Level);
        Assert.Contains("could not be removed", entry.Message, StringComparison.Ordinal);
        Assert.False(_log.WroteWithoutSource);
    }

    [Fact]
    public async Task Uninstall_KeepsConfigurationAndLogs()
    {
        // A reinstall finds the user's lists where they were left.
        await UninstallCommand.RunAsync(_setup, purge: false, _output, _error);

        Assert.True(Directory.Exists(_paths.DataDirectory));
        Assert.True(File.Exists(_paths.ConfigFile));
        Assert.Contains("were kept", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_WithPurge_RemovesThemAndSaysItDid()
    {
        await UninstallCommand.RunAsync(_setup, purge: true, _output, _error);

        Assert.False(Directory.Exists(_paths.DataDirectory));
        Assert.Contains("configuration and logs were removed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_WithPurge_AFailedRemovalIsAFailedRun()
    {
        // The ordinary case: the stop step failed, the still-running service holds a log file open and the
        // delete cannot finish. Answering 0 would tell the MSI the machine is clean while the configuration
        // and logs are still on it.
        using var held = File.Open(_paths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None);

        var exit = await UninstallCommand.RunAsync(_setup, purge: true, _output, _error);

        Assert.Equal(4, exit);
        Assert.True(Directory.Exists(_paths.DataDirectory));
        Assert.Contains("could not be removed", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("not all of it", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_WithPurge_NamesWhatItIsAboutToRemoveBeforeItRemovesIt()
    {
        // The one step that destroys something installing again does not bring back.
        await UninstallCommand.RunAsync(_setup, purge: true, _output, _error);

        var said = _output.ToString();

        Assert.True(
            said.IndexOf("Removing the configuration", StringComparison.Ordinal)
            < said.IndexOf("configuration and logs were removed", StringComparison.Ordinal));

        Assert.Contains(_paths.DataDirectory, said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContextForThisMachineHandsTheManagerTheServiceAndTheSchedulerTheTool()
    {
        // Safe to build, and nothing else here builds it: every collaborator's constructor is trivial and
        // the hosts file is reached through a closure, so it opens no handle and registers nothing. This
        // also checks that the two paths are different files.
        var setup = SetupContext.ForThisMachine();

        Assert.Equal("Chronos.Service.exe", Path.GetFileName(setup.Definition.ImagePath));
        Assert.Equal(ServiceDefinition.ChronosServiceName, setup.Definition.Name);

        // The service manager gets the service; the scheduler gets this tool, since the boot task runs
        // 'chronos recover'. Same directory (the installer copies the whole product into one), not the same file.
        Assert.NotEqual(setup.Definition.ImagePath, setup.CommandPath);
        Assert.Equal(Path.GetDirectoryName(setup.CommandPath), Path.GetDirectoryName(setup.Definition.ImagePath));
        Assert.Same(ChronosPaths.Default, setup.Paths);
    }

    [Fact]
    public void ACommandThatNeedsAdministratorRightsSaysSoInsteadOfFailingAtThePlatform()
    {
        var exit = Administrator.Refuse("install", _error);

        // 2, the code for a command line this process cannot run, not 4: nothing was attempted, let alone left half done.
        Assert.Equal(2, exit);
        Assert.Contains("administrator rights", _error.ToString(), StringComparison.Ordinal);
    }

    private Task<CleanResult> CleanAsync(TextWriter output, TextWriter error)
    {
        _steps.Add("clean");

        if (_cleanThrows is { } failure)
        {
            throw failure;
        }

        if (_clean.RemovedSomething)
        {
            output.WriteLine("The Chronos filter provider was removed.");
        }

        if (_cleanSays is not null)
        {
            output.WriteLine(_cleanSays);
        }

        if (_cleanComplains is not null)
        {
            error.WriteLine(_cleanComplains);
        }

        return Task.FromResult(_clean);
    }

    private ProductKeyRemoval RemoveProductKey()
    {
        _steps.Add("remove-product-key");

        return _productKeyThrows is null ? _productKey : throw _productKeyThrows;
    }

    private CleanResult RestoreDnsAgain(TextWriter output, TextWriter error)
    {
        _steps.Add("restore-dns");
        output.WriteLine(_dnsAgainSays);

        if (_dnsAgainComplains is not null)
        {
            error.WriteLine(_dnsAgainComplains);
        }

        return _dnsAgain;
    }

    /// <summary>Makes one named installation step fail, and only that one.</summary>
    private void Arm(string step, Exception failure)
    {
        switch (step)
        {
            case "data-directory": _dataDirectoryThrows = failure; break;
            case "event-source": _log.EnsureSourceThrows = failure; break;
            case "install-service": _services.InstallThrows = failure; break;
            case "register-task": _tasks.RegisterThrows = failure; break;
            case "start-service": _services.StartThrows = failure; break;
            default: throw new ArgumentOutOfRangeException(nameof(step), step, "Installation has no such step.");
        }
    }

    private void ClearState()
    {
        _steps.Add("clear-state");

        if (_clearStateThrows is { } failure)
        {
            throw failure;
        }

        _stateCleared++;
    }

    private void CreateDataDirectory(string path)
    {
        _steps.Add("data-directory");

        if (_dataDirectoryThrows is { } failure)
        {
            throw failure;
        }

        // Directory.CreateDirectory, not DataDirectory.Create: the rights are DataDirectoryTests' business,
        // and setting them here would leave the temporary directory unreadable to the account that deletes it.
        Directory.CreateDirectory(path);
    }
}
