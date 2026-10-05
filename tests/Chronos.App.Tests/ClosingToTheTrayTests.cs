using System.Text;
using System.Text.RegularExpressions;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>
/// The tray icon assets and the two source lines that decide whether closing the window ends the
/// process. The view cannot be stood up without a windowing system, so the assets are opened for
/// real and the source is read as text. The decision itself is in <see cref="ExitWarningTests"/>.
/// </summary>
public sealed class ClosingToTheTrayTests
{
    /// <summary>
    /// The two tray icons and the product mark. The files are linked in from the repository; a broken link
    /// leaves them out or renamed without a build error. Read out of the assembly, since Avalonia's asset
    /// loader needs a running application.
    /// </summary>
    [Theory]
    [InlineData("/Assets/tray-on-32.png")]
    [InlineData("/Assets/tray-off-32.png")]
    [InlineData("/Assets/chronos.ico")]
    public void TheIconsAreInTheBuildWhereTheViewLooksForThem(string path)
    {
        Assert.Contains(path, Resources(), StringComparison.Ordinal);
    }

    /// <summary>Proves the reader looks at the right blob: a path nothing links in must be absent.</summary>
    [Fact]
    public void TheAssetReaderNoticesAPathThatIsNotThere()
    {
        Assert.DoesNotContain("/Assets/no-such-icon.png", Resources(), StringComparison.Ordinal);
    }

    private static string Resources()
    {
        var assembly = typeof(TrayViewModel).Assembly;

        using var stream = assembly.GetManifestResourceStream("!AvaloniaResources");
        Assert.True(stream is not null, "The interface's assembly carries no Avalonia resources at all.");

        using var memory = new MemoryStream();
        stream!.CopyTo(memory);

        // The index holds the paths as plain UTF-8 ahead of the files themselves.
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    /// <summary>The window cancels its own closing and hides, unless the process really is leaving.</summary>
    [Fact]
    public void TheWindowCancelsItsOwnClosingAndHidesInstead()
    {
        var source = Read("MainWindow.axaml.cs");

        Assert.Matches(new Regex(@"OnClosing\s*\(", RegexOptions.None), source);
        Assert.Contains("IsQuitting", source, StringComparison.Ordinal);
        Assert.Contains("Cancel = true", source, StringComparison.Ordinal);
        Assert.Contains("Hide()", source, StringComparison.Ordinal);
    }

    /// <summary>Avalonia shuts down when the last window closes by default, so the lifetime must end only when asked.</summary>
    [Fact]
    public void TheProcessOutlivesItsWindowsRatherThanEndingWithTheLastOne()
    {
        Assert.Contains(
            "ShutdownMode.OnExplicitShutdown",
            Read("App.axaml.cs"),
            StringComparison.Ordinal);
    }

    /// <summary>Proves the reader looks at something: a word certainly not in the file must be absent.</summary>
    [Fact]
    public void TheReaderIsLookingAtTheFileAndNotAtNothing()
    {
        var source = Read("MainWindow.axaml.cs");

        Assert.NotEmpty(source);
        Assert.DoesNotContain("ThisWordIsNotInTheFile", source, StringComparison.Ordinal);
        Assert.Contains("MainWindow", source, StringComparison.Ordinal);
    }

    private static string Read(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chronos.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "The solution root is not above the build output.");

        var path = Path.Combine(directory!.FullName, "src", "Chronos.App", file);
        Assert.True(File.Exists(path), $"{path} is not there.");

        return File.ReadAllText(path);
    }
}
