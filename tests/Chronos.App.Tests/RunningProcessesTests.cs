using System.ComponentModel;
using System.Diagnostics;
using Chronos.App.Apps;

namespace Chronos.App.Tests;

/// <summary>Pick from what is running. Dozens of processes cannot be looked into by an ordinary user, and the list must survive that.</summary>
public sealed class RunningProcessesTests
{
    /// <summary>Against the real machine: this process is in the list and the ones that refused cost nothing.</summary>
    [Fact]
    public void TheListNamesWhatIsRunningNowIncludingThisProcess()
    {
        using var self = Process.GetCurrentProcess();
        var mine = Path.GetFileName(self.MainModule?.FileName ?? string.Empty);
        Assert.False(string.IsNullOrEmpty(mine), "This process cannot see its own image path.");

        var running = RunningProcesses.Now();

        Assert.NotEmpty(running);
        Assert.Contains(running, app => app.Name.Equals(mine, StringComparison.OrdinalIgnoreCase));
        Assert.All(running, app => Assert.False(string.IsNullOrWhiteSpace(app.Path)));
    }

    /// <summary>A process the interface cannot open throws when asked for its image path; that is normal and is stepped over.</summary>
    [Fact]
    public void AProcessThatCannotBeLookedIntoIsSteppedOverRatherThanBreakingTheList()
    {
        var gathered = RunningProcesses.Gather(
        [
            () => @"C:\Games\game.exe",

            // Access is denied: an ordinary user asking a service process for its modules.
            () => throw new Win32Exception(5),

            // "Only part of a ReadProcessMemory request was completed": a 32-bit process seen
            // from a 64-bit one, which is most of what a fresh Windows install runs.
            () => throw new Win32Exception(299),

            // The process ended between being listed and being asked.
            () => throw new InvalidOperationException("Process has exited."),
            () => throw new NotSupportedException("Not supported on a remote process."),
            () => @"C:\Tools\editor.exe",
        ]);

        Assert.Equal(["editor.exe", "game.exe"], gathered.Select(app => app.Name));
    }

    /// <summary>The whole list refusing to be read is the same answer as an empty machine.</summary>
    [Fact]
    public void AMachineWhereNothingCanBeLookedIntoGivesAnEmptyListRatherThanAFailure()
    {
        var gathered = RunningProcesses.Gather([() => throw new Win32Exception(5), () => throw new Win32Exception(5)]);

        Assert.Empty(gathered);
    }

    /// <summary>Twenty copies of svchost.exe are one line, since the rule from any of them is the same.</summary>
    [Fact]
    public void TheSameProgramRunningManyTimesIsOneLineToPick()
    {
        var gathered = RunningProcesses.Gather(
        [
            () => @"C:\Windows\System32\svchost.exe",
            () => @"C:\Windows\System32\svchost.exe",
            () => @"c:\windows\system32\SVCHOST.EXE",
        ]);

        Assert.Single(gathered);
    }

    /// <summary>Same name in different places stays two lines; the match kind is about that difference.</summary>
    [Fact]
    public void TheSameNameInTwoPlacesStaysTwoLines()
    {
        var gathered = RunningProcesses.Gather(
        [
            () => @"C:\Games\Steam\game.exe",
            () => @"D:\Backup\game.exe",
        ]);

        Assert.Equal(2, gathered.Count);
        Assert.All(gathered, app => Assert.Equal("game.exe", app.Name));
    }

    [Fact]
    public void AProcessThatNamesNoFileIsSteppedOverToo()
    {
        var gathered = RunningProcesses.Gather([() => null, () => string.Empty, () => "   ", () => @"C:\a\real.exe"]);

        Assert.Equal(["real.exe"], gathered.Select(app => app.Name));
    }

    [Fact]
    public void TheListIsInAnOrderSomebodyCanReadDown()
    {
        var gathered = RunningProcesses.Gather(
        [
            () => @"C:\z\zebra.exe",
            () => @"C:\a\Apple.exe",
            () => @"C:\m\middle.exe",
        ]);

        Assert.Equal(["Apple.exe", "middle.exe", "zebra.exe"], gathered.Select(app => app.Name));
    }
}
