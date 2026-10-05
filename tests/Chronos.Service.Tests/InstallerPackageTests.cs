using System.Globalization;
using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>
/// What the built MSI says it will do, read out of the package and never run. The installer's work is
/// done by <c>chronos install</c> and <c>chronos uninstall</c>, which have their own tests; here the
/// package must call them in the right place in the sequence, with the rights they need, and carry
/// the files they need.
/// </summary>
public sealed class InstallerPackageTests
{
    /// <summary>Deferred: the action runs from the installation script, not while it is written.</summary>
    private const int Deferred = 1024;

    /// <summary>No impersonation: the action runs as the system, not as whoever double-clicked.</summary>
    private const int NoImpersonate = 2048;

    /// <summary>Carry on: a non-zero exit code from this action is not a failed installation.</summary>
    private const int IgnoresTheExitCode = 64;

    /// <summary>HKEY_CURRENT_USER, as the Registry table numbers the roots.</summary>
    private const string CurrentUser = "1";

    /// <summary>
    /// WIXCLOSEAPPLICATION_ATTRIBUTE_TERMINATEPROCESS, and its neighbour
    /// WIXCLOSEAPPLICATION_ATTRIBUTE_CLOSEMESSAGE, out of the extension's own header.
    /// </summary>
    private const int TerminatesTheProcess = 0x20;

    private const int SendsACloseMessage = 0x1;

    [RequiresInstaller]
    public void ThePackageInstallsTheProductForTheWholeMachine()
    {
        Assert.Equal("Chronos", Property("ProductName"));

        // In the package's own summary stream; the platform half is what ICE80 stops the build over when it is left out.
        Assert.Equal("x64;1033", InstallerPackage.Summary(InstallerPackage.SummaryTemplate));
        Assert.Equal("1", Property("ALLUSERS"));
    }

    /// <summary>One version number for the package and the programs in it, from Directory.Build.props.</summary>
    [RequiresInstaller]
    public void ThePackageCarriesTheVersionOfTheProgramsInIt()
    {
        var programs = typeof(Chronos.Service.Program).Assembly.GetName().Version!;

        Assert.Equal($"{programs.Major}.{programs.Minor}.{programs.Build}", Property("ProductVersion"));
        Assert.Equal(new Version(1, 0, 0), new Version(programs.Major, programs.Minor, programs.Build));
    }

    /// <summary>Self-contained: the runtime is in the package, so a machine needs no .NET installed.</summary>
    [RequiresInstaller]
    public void ThePackageCarriesTheRuntime()
    {
        var files = InstallerPackage.Query("SELECT `FileName` FROM `File`")
            .Select(row => row[0].Split('|').Last())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("hostfxr.dll", files);
        Assert.Contains("coreclr.dll", files);
        Assert.Contains("System.Private.CoreLib.dll", files);
        Assert.DoesNotContain("libSkiaSharp.pdb", files);
    }

    [RequiresInstaller]
    public void ThePackageRefusesToInstallWithoutAdministratorRights()
    {
        // There is no reduced mode to fall back to, so the refusal is a sentence rather than an access error in the middle of writing to Program Files.
        var refusal = Assert.Single(
            InstallerPackage.Query("SELECT `Condition`, `Description` FROM `LaunchCondition`"),
            row => row[0].Contains("Privileged", StringComparison.Ordinal));

        Assert.Contains("administrator", refusal[1], StringComparison.OrdinalIgnoreCase);
    }

    [RequiresInstaller]
    public void ThePackageCallsTheProductToRegisterItself()
    {
        // The service parameters, the recovery task and the event log source are described once, in the product; the package only calls it.
        var register = Action("ChronosRegister");

        Assert.Equal("install", register.Target);
        Assert.Equal(Deferred, register.Type & Deferred);
        Assert.Equal(NoImpersonate, register.Type & NoImpersonate);

        // On an installation and not on a removal: without this condition the register call could run on
        // the way out too, and an uninstall would end by registering the service it had just removed.
        Assert.Contains("NOT Installed", register.Condition, StringComparison.Ordinal);
    }

    [RequiresInstaller]
    public void ThePackageCallsTheProductToTakeItselfOffAgain()
    {
        // A machine whose MSI is gone and whose service is not still has one command that knows how to finish the job.
        //
        // NoImpersonate is why the autostart entry is not among what this call takes off. The action runs
        // as LocalSystem, where HKEY_CURRENT_USER is the service account's hive, which does not exist on
        // an ordinary machine, so the Disable() that InstallCommandTests pins reaches nobody's Run key.
        // The package owns that value itself: see ThePackageOwnsTheAutostartEntryOfTheAccountThatInstalls.
        var unregister = Action("ChronosUnregister");

        Assert.Equal("uninstall", unregister.Target);
        Assert.Equal(Deferred, unregister.Type & Deferred);
        Assert.Equal(NoImpersonate, unregister.Type & NoImpersonate);
    }

    [RequiresInstaller]
    public void ThePackageOwnsTheAutostartEntryOfTheAccountThatInstalls()
    {
        // The autostart entry on the MSI path: WriteRegistryValues puts this value in the installing user's
        // hive and RemoveRegistryValues takes it out, without a custom action reaching a hive it cannot see
        // from LocalSystem.
        //
        // The key and the name are pinned against the ones the product reads and writes, as
        // AutostartTests.TheUninstallerLooksForTheEntryThisWrites pins the interface's copy. An entry written
        // under a different name would not be found by the interface, and nothing would remove it.
        var entry = Assert.Single(
            InstallerPackage.Query("SELECT `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`"),
            row => string.Equals(row[2], UserAutostart.EntryName, StringComparison.Ordinal)
                && row[1].Contains("Run", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(CurrentUser, entry[0]);
        Assert.Equal(UserAutostart.RunKeyPath, entry[1]);

        // Quoted, because a Run entry is a command line and an unquoted one stops at the first
        // space: C:\Program Files\Chronos\Chronos.App.exe would be read as C:\Program.exe.
        Assert.Equal(@"""[INSTALLFOLDER]Chronos.App.exe""", entry[3]);

        // In the feature, which is what makes it go on uninstall: a component no feature carries is never installed or removed.
        Assert.Contains(
            InstallerPackage.Query("SELECT `Feature_`, `Component_` FROM `FeatureComponents`"),
            row => string.Equals(row[1], entry[4], StringComparison.Ordinal));
    }

    [RequiresInstaller]
    public void TheInterfaceIsClosedBeforeWindowsDecidesItsFilesAreInUse()
    {
        // RemoveFiles cannot delete a running executable, and the interface autostarts, so running is its
        // normal state. Without this the uninstall shows the files-in-use dialog, or under /qn schedules
        // the deletion for the next reboot and leaves the install folder and a live process behind.
        var close = Assert.Single(InstallerPackage.Query(
            "SELECT `Target`, `Attributes` FROM `Wix4CloseApplication`"));

        Assert.Equal("Chronos.App.exe", close[0]);

        var attributes = int.Parse(close[1], CultureInfo.InvariantCulture);

        // Terminated rather than sent WM_CLOSE: the interface deliberately does not exit on it (closing the
        // window hides it to the tray), so the polite message would leave the process holding the file.
        Assert.Equal(TerminatesTheProcess, attributes & TerminatesTheProcess);
        Assert.Equal(0, attributes & SendsACloseMessage);

        // Before RemoveFiles, not where the extension schedules it (before InstallFiles), which is too
        // late on the way out since RemoveFiles runs first and has already tried to delete the executable.
        Assert.True(Sequence("Wix4CloseApplications_X64") < Sequence("RemoveFiles"));

        // And not earlier than InstallInitialize: this immediate action schedules a deferred one, which is
        // only allowed between InstallInitialize and InstallFinalize. Scheduled before InstallValidate,
        // the package refused to install at all (error 2762, exit code 1603).
        Assert.True(Sequence("Wix4CloseApplications_X64") > Sequence("InstallInitialize"));
    }

    [RequiresInstaller]
    public void TheProductIsRegisteredAfterItsFilesAndUnregisteredBeforeTheyGo()
    {
        // The action is one of the files, so it cannot run before InstallFiles; and the event log source
        // must be unregistered while the executable whose message resources HKLM points at is still on
        // disk, or HKLM keeps a pointer into nothing.
        Assert.True(Sequence("ChronosRegister") > Sequence("InstallFiles"));
        Assert.True(Sequence("ChronosUnregister") < Sequence("RemoveFiles"));
        Assert.True(Sequence("ChronosUnregisterPurge") < Sequence("RemoveFiles"));
    }

    [RequiresInstaller]
    public void AStepTheRemovalCouldNotFinishDoesNotRollTheRemovalBack()
    {
        // `chronos uninstall` carries on past a step it could not do and answers 4 when any failed.
        // Checked, that 4 would fail the uninstall and put the files back on a machine whose service and
        // task were already removed. Installation is the other way round: a half-registered product must
        // not be left looking installed.
        Assert.Equal(IgnoresTheExitCode, Action("ChronosUnregister").Type & IgnoresTheExitCode);
        Assert.Equal(IgnoresTheExitCode, Action("ChronosUnregisterPurge").Type & IgnoresTheExitCode);
        Assert.Equal(0, Action("ChronosRegister").Type & IgnoresTheExitCode);
    }

    [RequiresInstaller]
    public void ThePackageKeepsTheConfigurationUnlessItIsAskedNotTo()
    {
        // The user's lists survive an uninstall unless asked otherwise: the package removes only what it
        // laid down (it has no RemoveFile table) and the data directory is not among it.
        Assert.False(InstallerPackage.HasTable("RemoveFile"));

        Assert.Contains("NOT REMOVEDATA", Action("ChronosUnregister").Condition, StringComparison.Ordinal);
        Assert.Equal("uninstall --purge", Action("ChronosUnregisterPurge").Target);
        Assert.Contains("AND REMOVEDATA", Action("ChronosUnregisterPurge").Condition, StringComparison.Ordinal);
    }

    [RequiresInstaller]
    public void TheOptionToRemoveTheDataSurvivesTheHandOffToTheElevatedActions()
    {
        // Without this the property is silently dropped on the way to the process that runs the deferred
        // actions, and an administrator who asked for the data to go would be told it went while it stayed.
        Assert.Contains("REMOVEDATA", Property("SecureCustomProperties"), StringComparison.Ordinal);
    }

    [RequiresInstaller]
    public void ThePackageCarriesTheServiceTheCliAndTheInterface()
    {
        foreach (var name in new[] { "Chronos.Service.exe", "chronos.exe", "Chronos.App.exe" })
        {
            Assert.Equal(string.Empty, InstallerPackage.DirectoryOf(name));
        }
    }

    [RequiresInstaller]
    public void ThePackageCarriesTheEventLogResourcesWhereWindowsWillLookForThem()
    {
        // The one part of event logging a package can get wrong on its own. EventLog.CreateEventSource
        // writes the path of System.Diagnostics.EventLog.Messages.dll into HKLM, taken from beside the
        // assembly that loads it. Without it, entries render in Event Viewer as "The description for Event
        // ID (2001) cannot be found". Self-contained, both lie in the installation folder.
        Assert.Equal(string.Empty, InstallerPackage.DirectoryOf("System.Diagnostics.EventLog.Messages.dll"));
        Assert.Equal(string.Empty, InstallerPackage.DirectoryOf("System.Diagnostics.EventLog.dll"));
    }

    [RequiresInstaller]
    public void TheStartMenuAndDesktopShortcutsOpenTheInterface()
    {
        var shortcuts = InstallerPackage.Query("SELECT `Directory_`, `Name`, `Target` FROM `Shortcut`");

        Assert.Equal(["DesktopFolder", "ProgramMenuFolder"], shortcuts.Select(row => row[0]).Order());
        Assert.All(shortcuts, row => Assert.Equal("[INSTALLFOLDER]Chronos.App.exe", row[2]));
    }

    /// <summary>
    /// The interface opens when an interactive installation finishes. From the UI sequence, which runs
    /// in the user's own unelevated msiexec; from the execute sequence it would start elevated.
    /// </summary>
    [RequiresInstaller]
    public void TheInterfaceOpensWhenTheInstallationFinishes()
    {
        var launch = InstallerPackage.Query("SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`")
            .Single(row => row[0] == "LaunchChronos");
        var ui = Row("LaunchChronos", "InstallUISequence");

        Assert.Equal("WixShellExec", launch[3]);
        Assert.Equal(0, int.Parse(launch[1], CultureInfo.InvariantCulture) & (Deferred | NoImpersonate));
        Assert.True(int.Parse(ui[2], CultureInfo.InvariantCulture) > int.Parse(Row("ExecuteAction", "InstallUISequence")[2], CultureInfo.InvariantCulture));
        Assert.Contains("REMOVE", ui[1], StringComparison.Ordinal);
        Assert.DoesNotContain(InstallerPackage.Query("SELECT `Action` FROM `InstallExecuteSequence`"), row => row[0] == "LaunchChronos");
        Assert.Equal("[INSTALLFOLDER]Chronos.App.exe", SetPropertyValue("WixShellExecTarget"));
    }

    /// <summary>
    /// No "close these applications" prompt: the package closes the interface itself before its files
    /// go, and opens it again after an interactive installation, so Restart Manager has nothing to ask.
    /// </summary>
    [RequiresInstaller]
    public void TheInstallerDoesNotAskToCloseTheInterface()
    {
        Assert.Equal("Disable", Property("MSIRESTARTMANAGERCONTROL"));

        // Ended quietly before InstallValidate, which is where the files-in-use prompt comes from.
        Assert.True(Sequence("CloseInterfaceEarly") < Sequence("InstallValidate"));
        Assert.Equal("WixQuietExec", Action("CloseInterfaceEarly").Target);
        Assert.Contains("taskkill.exe\" /F /IM Chronos.App.exe", SetPropertyValue("WixQuietExecCmdLine"), StringComparison.Ordinal);
    }

    [RequiresInstaller]
    public void TheProductHasItsIconInTheListOfInstalledApps()
    {
        var icon = Property("ARPPRODUCTICON");

        Assert.Contains(InstallerPackage.Query("SELECT `Name` FROM `Icon`"), row => row[0] == icon);
    }

    /// <summary>A rebuild of the same version replaces the installed one instead of installing beside it.</summary>
    [RequiresInstaller]
    public void ASameVersionInstallationReplacesTheInstalledOne()
    {
        const int VersionMaxInclusive = 0x200;
        var upgrade = InstallerPackage.Query("SELECT `VersionMax`, `Attributes` FROM `Upgrade`")
            .Single(row => !string.IsNullOrEmpty(row[0]));

        Assert.Equal(Property("ProductVersion"), upgrade[0]);
        Assert.NotEqual(0, int.Parse(upgrade[1], CultureInfo.InvariantCulture) & VersionMaxInclusive);
    }

    /// <summary>The value a SetProperty action (type 51) gives the named property.</summary>
    private static string SetPropertyValue(string property) =>
        InstallerPackage.Query("SELECT `Action`, `Source`, `Target` FROM `CustomAction`")
            .Single(row => string.Equals(row[1], property, StringComparison.Ordinal))[2];

    private static string Property(string name) =>
        InstallerPackage.Query("SELECT `Property`, `Value` FROM `Property`")
            .Single(row => string.Equals(row[0], name, StringComparison.Ordinal))[1];

    /// <summary>Where an action stands in the installation sequence. MSI compares the numbers, which is how "after the files" and "before they go" are checked without running one.</summary>
    private static int Sequence(string action) =>
        int.Parse(Row(action)[2], CultureInfo.InvariantCulture);

    private static (int Type, string Target, string Condition) Action(string id)
    {
        var row = InstallerPackage.Query("SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`")
            .Single(candidate => string.Equals(candidate[0], id, StringComparison.Ordinal));

        return (
            int.Parse(row[1], CultureInfo.InvariantCulture),
            row[3],
            Row(id)[1]);
    }

    private static IReadOnlyList<string> Row(string action, string table = "InstallExecuteSequence") =>
        InstallerPackage.Query($"SELECT `Action`, `Condition`, `Sequence` FROM `{table}`")
            .Single(row => string.Equals(row[0], action, StringComparison.Ordinal));
}
