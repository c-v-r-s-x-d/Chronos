using Chronos.Core.Rules;
using Chronos.Ipc;
using Chronos.Service.Rules;

namespace Chronos.Service.Tests;

public sealed class WindowsProtectedAppPolicyTests
{
    private readonly WindowsProtectedAppPolicy _policy = new();

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("EXPLORER.EXE")]
    [InlineData("winlogon.exe")]
    [InlineData("csrss.exe")]
    [InlineData("lsass.exe")]
    [InlineData("services.exe")]
    [InlineData("svchost.exe")]
    public void ProtectsSystemProcessesByName(string name)
    {
        var verdict = _policy.Evaluate(new AppRule(AppMatchKind.FileName, name));

        Assert.True(verdict.IsProtected);
        Assert.Equal(IpcCodes.RulesAppProtectedSystemFile, verdict.Reason);
    }

    [Fact]
    public void ProtectsAnythingUnderSystem32()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        var verdict = _policy.Evaluate(new AppRule(AppMatchKind.FullPath, path));

        Assert.True(verdict.IsProtected);

        // A code, not a sentence around the path: the path never leaves the service above Debug.
        Assert.Equal(IpcCodes.RulesAppProtectedSystemDirectory, verdict.Reason);
    }

    [Fact]
    public void ProtectsAnythingUnderTheWindowsDirectoryItself()
    {
        // The protected set is the Windows system directories, and System32 is not all of them: components whose absence stops the Start menu opening live beside it.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(windows, "SystemApps", "ShellExperienceHost.exe");

        Assert.True(_policy.Evaluate(new AppRule(AppMatchKind.FullPath, path)).IsProtected);
    }

    [Fact]
    public void DoesNotProtectADirectoryThatMerelyStartsWithAProtectedOne()
    {
        // Without a separator in the comparison, C:\WindowsExtra reads as C:\Windows.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.False(_policy.Evaluate(new AppRule(AppMatchKind.FullPath, $@"{windows}Extra\game.exe")).IsProtected);
    }

    [Fact]
    public void ProtectsChronosItself()
    {
        Assert.True(_policy.Evaluate(new AppRule(AppMatchKind.FileName, "Chronos.Service.exe")).IsProtected);
        Assert.True(_policy.Evaluate(new AppRule(AppMatchKind.FileName, "chronos.exe")).IsProtected);
    }

    [Fact]
    public void AllowsAnOrdinaryApplication()
    {
        var verdict = _policy.Evaluate(new AppRule(AppMatchKind.FullPath, @"C:\Games\game.exe"));

        Assert.False(verdict.IsProtected);
        Assert.Null(verdict.Reason);
    }

    [Fact]
    public void ProtectsBySystemDirectoryEvenWhenTheRuleIsAName()
    {
        Assert.True(_policy.Evaluate(new AppRule(AppMatchKind.FileName, "lsass.exe")).IsProtected);
    }
}
