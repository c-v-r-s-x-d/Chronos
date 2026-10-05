using Chronos.Service.Configuration;

namespace Chronos.Service.Tests;

public sealed class ChronosPathsTests
{
    [Fact]
    public void FilesLiveDirectlyUnderTheDataDirectory()
    {
        var paths = new ChronosPaths(@"C:\Data\Chronos", @"C:\Users\x\Chronos");

        Assert.Equal(@"C:\Data\Chronos\config.json", paths.ConfigFile);
        Assert.Equal(@"C:\Data\Chronos\state.json", paths.StateFile);
        Assert.Equal(@"C:\Data\Chronos\dns-backup.json", paths.DnsBackupFile);
        Assert.Equal(@"C:\Data\Chronos\logs", paths.ServiceLogDirectory);
        Assert.Equal(@"C:\Users\x\Chronos\logs", paths.UserLogDirectory);
    }

    [Fact]
    public void TheDnsBackupIsAFileOfItsOwnBesideTheSessionState()
    {
        // Two independent sources are needed. A copy kept inside state.json would be cleared at the end of
        // a session and thrown away whole when the session store cannot read that file.
        var paths = new ChronosPaths(@"C:\Data\Chronos", @"C:\Users\x\Chronos");

        Assert.NotEqual(paths.StateFile, paths.DnsBackupFile);
        Assert.Equal(paths.DataDirectory, Path.GetDirectoryName(paths.DnsBackupFile));
    }

    [Fact]
    public void EnsureDataDirectoryExists_CreatesItAndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));
        var paths = new ChronosPaths(root, Path.Combine(root, "user"));

        try
        {
            paths.EnsureDataDirectoryExists();
            paths.EnsureDataDirectoryExists();

            Assert.True(Directory.Exists(paths.DataDirectory));
            Assert.True(Directory.Exists(paths.ServiceLogDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Default_PointsAtProgramDataAndLocalAppData()
    {
        var expectedData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Chronos");

        Assert.Equal(expectedData, ChronosPaths.Default.DataDirectory);
        Assert.Contains("Chronos", ChronosPaths.Default.UserLogDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsEmptyDirectories()
    {
        Assert.Throws<ArgumentException>(() => new ChronosPaths("  ", @"C:\x"));
        Assert.Throws<ArgumentException>(() => new ChronosPaths(@"C:\x", "  "));
    }
}
