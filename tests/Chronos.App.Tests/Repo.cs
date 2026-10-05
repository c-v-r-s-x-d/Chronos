namespace Chronos.App.Tests;

/// <summary>The interface's own files, found from the build output by way of the solution file. Shared by the tests that read markup.</summary>
internal static class Repo
{
    /// <summary>A path inside <c>src/Chronos.App</c>, asserted to exist.</summary>
    public static string App(params string[] parts)
    {
        var path = Path.Combine([Root(), "src", "Chronos.App", .. parts]);
        Assert.True(File.Exists(path) || Directory.Exists(path), $"{path} is not there.");

        return path;
    }

    /// <summary>A path inside the repository's <c>assets</c> folder.</summary>
    public static string Asset(params string[] parts) => Path.Combine([Root(), "assets", .. parts]);

    /// <summary>Every piece of the interface's markup.</summary>
    public static IReadOnlyList<string> Markup()
    {
        var files = Directory.EnumerateFiles(App(), "*.axaml", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(files);

        return files;
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chronos.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "The solution root is not above the build output.");

        return directory!.FullName;
    }
}
