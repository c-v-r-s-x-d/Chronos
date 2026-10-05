using System.Diagnostics;
using Chronos.Service.Apps;

namespace Chronos.Service.Tests;

public sealed class WindowsProcessControlTests
{
    [Fact]
    public void List_FindsThisProcessWithItsImagePath()
    {
        var control = new WindowsProcessControl();

        var self = control.List().SingleOrDefault(process => process.ProcessId == Environment.ProcessId);

        Assert.NotEqual(default, self);
        Assert.EndsWith(".exe", self.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(self.ImagePath), $"'{self.ImagePath}' should be a real file.");
    }

    [Fact]
    public void List_ReadsMoreImagePathsThanMainModuleCan()
    {
        // The reason this class exists rather than Process.MainModule: MainModule throws for processes of another session or bitness.
        var viaMainModule = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                _ = process.MainModule!.FileName;
                viaMainModule++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        }

        Assert.True(
            new WindowsProcessControl().List().Count >= viaMainModule,
            "QueryFullProcessImageName must not see fewer processes than MainModule.");
    }

    [Fact]
    public void ImagePathOf_ReadsThePathOfARunningProcess()
    {
        // Turns the bare executable name the fallback source reports into something the protected-directory check can test.
        var path = new WindowsProcessControl().ImagePathOf(Environment.ProcessId);

        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"'{path}' should be a real file.");
    }

    [Fact]
    public void Kill_EndsAProcessWeStartedOurselves()
    {
        // The only process a test may terminate is one it owns.
        using var victim = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause") { UseShellExecute = false });
        Assert.NotNull(victim);

        Assert.Equal(KillOutcome.Terminated, new WindowsProcessControl().Kill(victim.Id));

        Assert.True(victim.WaitForExit(10_000), "The process should have exited.");
    }

    [Fact]
    public void Kill_ReportsFalseForAProcessThatIsAlreadyGone()
    {
        // A process that ended on its own between the event and the kill is not
        // a failure, and must not be logged as one.
        using var victim = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false });
        Assert.NotNull(victim);
        victim.WaitForExit(10_000);

        Assert.Equal(KillOutcome.AlreadyGone, new WindowsProcessControl().Kill(victim.Id));
    }

    [Fact]
    public void Kill_ReportsDeniedForALiveProcessThisAccountMayNotTouch()
    {
        // PID 4 is the System process: it exists, and no user-mode caller may terminate it.
        // Conflating this with "already gone" would let a block fail in silence.
        Assert.Equal(KillOutcome.Denied, new WindowsProcessControl().Kill(4));
    }
}
