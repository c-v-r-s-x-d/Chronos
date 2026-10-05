using Chronos.Core.Rules;
using Chronos.Service.Apps;

namespace Chronos.Service.Tests;

public sealed class AppRuleIndexTests
{
    private static AppRule ByPath(string value) => new(AppMatchKind.FullPath, value);

    private static AppRule ByName(string value) => new(AppMatchKind.FileName, value);

    [Fact]
    public void MatchesFullPath_AFullPathRuleOnlyForThatPath()
    {
        var index = AppRuleIndex.Build([ByPath(@"D:\games\game.exe")]);

        Assert.True(index.MatchesFullPath(@"D:\games\game.exe"));
        // The same file name in another directory is a different application.
        Assert.False(index.MatchesFullPath(@"D:\work\game.exe"));
    }

    [Fact]
    public void MatchesFullPath_AFullPathRuleRegardlessOfCase()
    {
        var index = AppRuleIndex.Build([ByPath(@"D:\Games\Game.exe")]);

        Assert.True(index.MatchesFullPath(@"d:\games\GAME.EXE"));
    }

    [Fact]
    public void MatchesFullPath_DoesNotMatchABareFileNameOfTheSameExecutable()
    {
        // A process-start event carries only a bare name, never a path, so a full-path rule must not match it.
        var index = AppRuleIndex.Build([ByPath(@"D:\games\game.exe")]);

        Assert.True(index.MatchesFullPath(@"D:\games\game.exe"));
        Assert.False(index.MatchesFullPath("game.exe"));
    }

    [Fact]
    public void MatchesFileName_AFileNameRuleWhereverTheApplicationLives()
    {
        // An application that moves to a new versioned directory on every update is why file-name matching exists.
        var index = AppRuleIndex.Build([ByName("game.exe")]);

        Assert.True(index.MatchesFileName(@"D:\games\app-1.2.3\game.exe"));
        Assert.True(index.MatchesFileName(@"D:\games\app-1.2.4\game.exe"));
    }

    [Fact]
    public void MatchesFileName_AFileNameRuleGivenWithAPath()
    {
        // A user who types a whole path into the file-name field means the file name.
        var index = AppRuleIndex.Build([ByName(@"D:\games\game.exe")]);

        Assert.True(index.MatchesFileName(@"E:\elsewhere\game.exe"));
    }

    [Fact]
    public void MatchesFileName_DoesNotMatchAFullPathRuleForTheSameExecutable()
    {
        var index = AppRuleIndex.Build([ByPath(@"D:\games\game.exe")]);

        Assert.False(index.MatchesFileName(@"D:\games\game.exe"));
        Assert.False(index.MatchesFileName("game.exe"));
    }

    [Fact]
    public void Matches_IsFalseForAnEmptyIndex()
    {
        var index = AppRuleIndex.Build([]);

        Assert.True(index.IsEmpty);
        Assert.False(index.MatchesFileName(@"D:\games\game.exe"));
        Assert.False(index.MatchesFullPath(@"D:\games\game.exe"));
    }

    [Fact]
    public void Matches_IsFalseForAnEmptyOrBlankImagePath()
    {
        var index = AppRuleIndex.Build([ByName("game.exe"), ByPath(@"D:\games\game.exe")]);

        Assert.False(index.MatchesFileName(""));
        Assert.False(index.MatchesFileName("   "));
        Assert.False(index.MatchesFullPath(""));
        Assert.False(index.MatchesFullPath("   "));
    }

    [Fact]
    public void Build_NormalisesRulePathsSoAShortFormRuleStillMatches()
    {
        // The 8.3 form only resolves for a file that exists, so the fixture is real and has a space in its
        // directory name to get a short form.
        var directory = Path.Combine(Path.GetTempPath(), $"chronos tests {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var full = Path.Combine(directory, "game.exe");
        File.WriteAllText(full, "");

        try
        {
            var index = AppRuleIndex.Build([ByPath(ShortPath.Of(full))]);

            Assert.True(index.MatchesFullPath(full));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Build_SkipsARuleWhoseKindIsNotOneOfTheDefinedOnes()
    {
        // Widening an unknown kind into the file-name bucket would turn one specific path into a name
        // match against every directory.
        var index = AppRuleIndex.Build([new AppRule((AppMatchKind)7, @"D:\games\game.exe")]);

        Assert.True(index.IsEmpty);
        Assert.False(index.MatchesFileName(@"D:\games\game.exe"));
        Assert.False(index.MatchesFullPath(@"D:\games\game.exe"));
        Assert.False(index.MatchesFileName(@"E:\elsewhere\game.exe"));
    }

    [Fact]
    public void HasFullPathRules_IsFalseForAFileNameOnlySet()
    {
        var index = AppRuleIndex.Build([ByName("game.exe")]);

        Assert.False(index.HasFullPathRules);
    }

    [Fact]
    public void HasFullPathRules_IsTrueWhenAtLeastOneRuleIsByFullPath()
    {
        var index = AppRuleIndex.Build([ByName("game.exe"), ByPath(@"D:\games\game.exe")]);

        Assert.True(index.HasFullPathRules);
    }

    [Fact]
    public void HasFullPathRules_IsFalseForAnEmptyIndex()
    {
        var index = AppRuleIndex.Build([]);

        Assert.False(index.HasFullPathRules);
    }

    [Fact]
    public void SameAs_IsTrueForTheSameRulesInAnotherOrder()
    {
        var one = AppRuleIndex.Build([ByName("a.exe"), ByPath(@"D:\b.exe")]);
        var other = AppRuleIndex.Build([ByPath(@"D:\b.exe"), ByName("a.exe")]);

        Assert.True(one.SameAs(other));
    }

    [Fact]
    public void SameAs_IsFalseWhenARuleIsAddedOrItsKindChanges()
    {
        var one = AppRuleIndex.Build([ByName("a.exe")]);

        Assert.False(one.SameAs(AppRuleIndex.Build([ByName("a.exe"), ByName("b.exe")])));
        Assert.False(one.SameAs(AppRuleIndex.Build([ByPath("a.exe")])));
    }

    [Fact]
    public void Matching_DoesNotDependOnTheNumberOfRules()
    {
        // The index is a set lookup, so a miss against 200 rules costs what a miss against one costs.
        var many = Enumerable.Range(0, 200).Select(i => ByName($"app{i}.exe")).ToArray();
        var index = AppRuleIndex.Build(many);

        Assert.True(index.MatchesFileName(@"D:\x\app199.exe"));
        Assert.False(index.MatchesFileName(@"D:\x\absent.exe"));
        Assert.Equal(200, index.Count);
    }
}
