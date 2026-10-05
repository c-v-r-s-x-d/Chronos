namespace Chronos.Core.Tests;

internal static class RepoPath
{
    public static string Resolve(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chronos.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Repository root with Chronos.slnx not found.");
        }

        return Path.Combine(dir.FullName, relative);
    }
}
