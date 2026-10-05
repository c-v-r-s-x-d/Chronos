using System.Text.RegularExpressions;
using SkiaSharp;

namespace Chronos.App.Tests;

public sealed class FontTests
{
    [Theory]
    [InlineData("Forum-Regular.ttf")]
    [InlineData("Cinzel.ttf")]
    public void TheFontShipsInsideTheApplication(string file)
    {
        // AssetLoader needs a running Avalonia; the manifest and the project file say the same thing.
        Assert.Contains("!AvaloniaResources", typeof(App).Assembly.GetManifestResourceNames());
        Assert.True(File.Exists(Repo.Asset("fonts", file)), $"{file} is not in assets/fonts.");

        var project = File.ReadAllText(Repo.App("Chronos.App.csproj"));
        Assert.Matches(@"<AvaloniaResource\s+Include=""[^""]*assets\\fonts\\\*\.ttf""\s+LinkBase=""Assets\\Fonts""", project);
    }

    /// <summary>The folder the palette names must be the folder the fonts are embedded under; elsewhere, drawing the first heading throws.</summary>
    [Theory]
    [InlineData("Forum-Regular.ttf")]
    [InlineData("Cinzel.ttf")]
    public void TheFontIsEmbeddedInTheFolderThePaletteNames(string file)
    {
        var markup = File.ReadAllText(Repo.App("Theme", "Palette.axaml"));
        var folders = Regex.Matches(markup, @"<FontFamily[^>]*>avares://Chronos\.App(?<folder>/[^<#]*)#")
            .Select(match => match.Groups["folder"].Value)
            .Distinct()
            .ToArray();

        var folder = Assert.Single(folders);

        // The index at the head of the resource blob lists every embedded path behind its length, so this matches the whole path.
        using var resources = typeof(App).Assembly.GetManifestResourceStream("!AvaloniaResources")!;
        using var reader = new StreamReader(resources, System.Text.Encoding.Latin1);
        var index = reader.ReadToEnd();
        var path = $"{folder}/{file}";

        Assert.Contains((char)path.Length + path, index, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeadingFontHasCyrillic()
    {
        using var typeface = SKTypeface.FromFile(Repo.Asset("fonts", "Forum-Regular.ttf"));

        Assert.True(typeface.ContainsGlyphs("НОВАЯ СЕССИЯ Защита ёЁ"));
    }

    [Theory]
    [InlineData("OFL-Forum.txt")]
    [InlineData("OFL-Cinzel.txt")]
    public void TheLicenceTravelsWithTheBuild(string file) =>
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "licenses", file)));

    [Theory]
    [InlineData("Chronos.HeadingFont", "Forum-Regular.ttf")]
    [InlineData("Chronos.LogoFont", "Cinzel.ttf")]
    public void TheFontFamilyNameIsTheOneInsideTheFile(string key, string file)
    {
        var markup = File.ReadAllText(Repo.App("Theme", "Palette.axaml"));
        var name = Regex.Match(markup, $@"<FontFamily\s+x:Key=""{Regex.Escape(key)}"">[^<#]*#(?<name>[^<]+)</FontFamily>").Groups["name"].Value;

        using var typeface = SKTypeface.FromFile(Repo.Asset("fonts", file));

        Assert.Equal(typeface.FamilyName, name);
    }
}
