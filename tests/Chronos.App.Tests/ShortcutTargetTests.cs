using System.Reflection;
using Chronos.App.Apps;

namespace Chronos.App.Tests;

/// <summary>
/// A shortcut is followed to the program it starts, and the rule is made for that program. Every
/// shortcut is a real <c>.lnk</c>, written through <c>WScript.Shell</c> while the resolver uses
/// <c>IShellLink</c>, so the test does not build files only the code under test can read.
/// </summary>
public sealed class ShortcutTargetTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "chronos-shortcuts-" + Guid.NewGuid().ToString("N"));

    public ShortcutTargetTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that will not go is the machine's business, not this test's.
        }
    }

    [Fact]
    public void AShortcutIsFollowedToTheProgramItStarts()
    {
        var program = Make("game.exe", "not really a program, but a real file");
        var shortcut = Link("Game.lnk", program);

        var resolved = Shortcut.Resolve(shortcut);

        Assert.Equal(ShortcutOutcome.Resolved, resolved.Outcome);
        Assert.True(resolved.IsResolved);
        Assert.Equal(program, resolved.Path, StringComparer.OrdinalIgnoreCase);

        // A rule on the .lnk matches nothing that ever runs.
        Assert.DoesNotContain(".lnk", resolved.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AShortcutWhoseProgramIsGoneIsRefusedRatherThanThrowing()
    {
        var program = Make("gone.exe", "here for now");
        var shortcut = Link("Gone.lnk", program);
        File.Delete(program);

        var resolved = Shortcut.Resolve(shortcut);

        Assert.Equal(ShortcutOutcome.TargetMissing, resolved.Outcome);
        Assert.False(resolved.IsResolved);
    }

    [Fact]
    public void AFileThatIsNotAShortcutIsRefusedRatherThanThrowing()
    {
        var text = Make("notes.txt", "this is not a shortcut");

        var resolved = Shortcut.Resolve(text);

        Assert.Equal(ShortcutOutcome.NotAShortcut, resolved.Outcome);
    }

    /// <summary>A file called <c>.lnk</c> that holds rubbish passes any name check and must be refused by the loader.</summary>
    [Fact]
    public void AFileNamedLikeAShortcutButHoldingRubbishIsRefusedRatherThanThrowing()
    {
        var pretend = Make("Broken.lnk", "L\0 this is not the shell link format at all");

        var resolved = Shortcut.Resolve(pretend);

        Assert.Equal(ShortcutOutcome.NotAShortcut, resolved.Outcome);
    }

    [Fact]
    public void AShortcutToAFolderNamesNoProgramToBlock()
    {
        var folder = Path.Combine(_directory, "somewhere");
        Directory.CreateDirectory(folder);
        var shortcut = Link("Folder.lnk", folder);

        var resolved = Shortcut.Resolve(shortcut);

        Assert.Equal(ShortcutOutcome.NoTarget, resolved.Outcome);
    }

    [Fact]
    public void AShortcutThatIsNotThereIsRefusedRatherThanThrowing()
    {
        var resolved = Shortcut.Resolve(Path.Combine(_directory, "never-existed.lnk"));

        Assert.Equal(ShortcutOutcome.NotAShortcut, resolved.Outcome);
    }

    /// <summary>Every refusal says something, and no two say the same, since each has a different way out.</summary>
    [Fact]
    public void EveryRefusalIsWordedAndNoTwoOfThemAreTheSame()
    {
        var refused = new[] { ShortcutOutcome.NotAShortcut, ShortcutOutcome.NoTarget, ShortcutOutcome.TargetMissing };

        var said = refused.Select(Shortcut.Refusal).ToArray();

        Assert.All(said, sentence => Assert.False(string.IsNullOrWhiteSpace(sentence)));
        Assert.Equal(refused.Length, said.Distinct(StringComparer.Ordinal).Count());

        // Nothing to say about a shortcut that worked.
        Assert.Equal(string.Empty, Shortcut.Refusal(ShortcutOutcome.Resolved));
    }

    private string Make(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);

        return path;
    }

    /// <summary>A real shortcut written by the Windows Script Host, late bound because that object has no useful type library.</summary>
    private string Link(string name, string target)
    {
        var path = Path.Combine(_directory, name);

        var type = Type.GetTypeFromProgID("WScript.Shell");
        Assert.True(type is not null, "WScript.Shell is not registered on this machine.");

        var shell = Activator.CreateInstance(type!);
        Assert.NotNull(shell);

        var link = shell!.GetType().InvokeMember(
            "CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
        Assert.NotNull(link);

        link!.GetType().InvokeMember(
            "TargetPath", BindingFlags.SetProperty, null, link, [target]);
        link.GetType().InvokeMember(
            "Save", BindingFlags.InvokeMethod, null, link, []);

        Assert.True(File.Exists(path), $"{path} was not written.");

        return path;
    }
}
