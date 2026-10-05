using Chronos.Service.Apps;

namespace Chronos.Service.Tests;

public sealed class ImagePathTests
{
    [Fact]
    public void Normalize_ExpandsEnvironmentVariables()
    {
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Equal(expected, ImagePath.Normalize("%SystemRoot%"), ignoreCase: true);
    }

    [Fact]
    public void Normalize_ResolvesTheShortEightDotThreeForm()
    {
        // A rule written with a full path must still match a process
        // whose image path arrived in the 8.3 form.
        var full = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var shortForm = ShortPath.Of(full);

        Assert.Equal(full, ImagePath.Normalize(shortForm), ignoreCase: true);
    }

    [Fact]
    public void Normalize_MakesARelativePathFull()
    {
        Assert.Equal(Path.GetFullPath("game.exe"), ImagePath.Normalize("game.exe"));
    }

    [Fact]
    public void Normalize_KeepsAPathThatDoesNotExist()
    {
        const string missing = @"D:\not installed\game.exe";

        Assert.Equal(missing, ImagePath.Normalize(missing), ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\Device\HarddiskVolume3\Windows\notepad.exe")]
    [InlineData("<not a path at all>")]
    public void Normalize_NeverThrows(string input)
    {
        // A bad rule in a hand-edited config must not take the whole layer down.
        _ = ImagePath.Normalize(input);
    }

    [Fact]
    public void FileNameOf_TakesTheLastSegmentOfEitherPathShape()
    {
        Assert.Equal("game.exe", ImagePath.FileNameOf(@"D:\games\game.exe"));
        Assert.Equal("notepad.exe", ImagePath.FileNameOf(@"\Device\HarddiskVolume3\Windows\notepad.exe"));
        Assert.Equal("game.exe", ImagePath.FileNameOf("game.exe"));
    }
}
