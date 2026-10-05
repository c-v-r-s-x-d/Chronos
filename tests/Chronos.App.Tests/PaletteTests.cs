using System.Globalization;
using System.Text.RegularExpressions;

namespace Chronos.App.Tests;

public sealed class PaletteTests
{
    // Foreground, background.
    private static readonly (string Fore, string Back)[] Pairs =
    [
        ("Text", "Background"), ("Text", "Sidebar"), ("Text", "Surface"),
        ("Heading", "Background"), ("Heading", "Sidebar"), ("Heading", "Surface"),
        ("Muted", "Background"), ("Muted", "Sidebar"), ("Muted", "Surface"),
        ("Selected", "Sidebar"), ("OnAccent", "Accent"),
        ("Ok", "Background"), ("Ok", "Surface"),
        ("Warn", "Background"), ("Warn", "Surface"),
        ("Error", "Background"), ("Error", "Surface"),
    ];

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void EveryPairInTheAppendixIsReadable(string theme)
    {
        var colors = Theme(theme);

        foreach (var (fore, back) in Pairs)
        {
            var ratio = Contrast(colors[fore], colors[back]);
            Assert.True(ratio >= 4.5, $"{theme}: {fore} on {back} is {ratio:F2}:1.");
        }
    }

    [Fact]
    public void BothThemesDefineTheSameColours() =>
        Assert.Equal(Theme("Light").Keys.Order(), Theme("Dark").Keys.Order());

    [Theory]
    [InlineData("Dark", "Background", "#15161A")]
    [InlineData("Light", "Background", "#F6F3EC")]
    [InlineData("Dark", "Sidebar", "#101114")]
    [InlineData("Light", "Sidebar", "#EDE8DC")]
    [InlineData("Dark", "Surface", "#1C1B18")]
    [InlineData("Light", "Surface", "#FFFFFF")]
    [InlineData("Dark", "Divider", "#2A2720")]
    [InlineData("Light", "Divider", "#DDD4C0")]
    [InlineData("Dark", "Border", "#3A352A")]
    [InlineData("Light", "Border", "#CFC4AC")]
    [InlineData("Dark", "Text", "#ECE6D8")]
    [InlineData("Light", "Text", "#2B2822")]
    [InlineData("Dark", "Heading", "#E7D7B0")]
    [InlineData("Light", "Heading", "#3A3122")]
    [InlineData("Dark", "Muted", "#8F897C")]
    [InlineData("Light", "Muted", "#67615A")]
    [InlineData("Dark", "Accent", "#C9A45C")]
    [InlineData("Light", "Accent", "#8A6A24")]
    [InlineData("Dark", "OnAccent", "#1A150A")]
    [InlineData("Light", "OnAccent", "#FFFFFF")]
    [InlineData("Dark", "Selected", "#E7C983")]
    [InlineData("Light", "Selected", "#6E5316")]
    [InlineData("Dark", "Ok", "#8FBF7A")]
    [InlineData("Light", "Ok", "#377033")]
    [InlineData("Dark", "Warn", "#D9A441")]
    [InlineData("Light", "Warn", "#8F600C")]
    [InlineData("Dark", "Error", "#D0705A")]
    [InlineData("Light", "Error", "#A8432F")]
    public void TheColoursAreTheOnesTheSpecificationNames(string theme, string role, string hex) =>
        Assert.Equal(hex, Theme(theme)[role], ignoreCase: true);

    [Fact]
    public void TheFluentAccentIsThePalettesAndNotTheSystems()
    {
        var app = File.ReadAllText(Repo.App("App.axaml"));

        Assert.Matches(@"<ColorPaletteResources\s+x:Key=""Light""[^>]*Accent=""#FF8A6A24""", app);
        Assert.Matches(@"<ColorPaletteResources\s+x:Key=""Dark""[^>]*Accent=""#FFC9A45C""", app);
    }

    private static Dictionary<string, string> Theme(string theme)
    {
        var markup = File.ReadAllText(Repo.App("Theme", "Palette.axaml"));
        var block = Regex.Match(
            markup,
            $@"<ResourceDictionary\s+x:Key=""{theme}"">(?<body>.*?)</ResourceDictionary>",
            RegexOptions.Singleline).Groups["body"].Value;

        Assert.False(string.IsNullOrEmpty(block), $"{theme} is not in the palette.");

        return Regex.Matches(block, @"<Color\s+x:Key=""Chronos\.(?<role>\w+)Color"">(?<hex>#[0-9A-Fa-f]{6})</Color>")
            .ToDictionary(m => m.Groups["role"].Value, m => m.Groups["hex"].Value);
    }

    private static double Contrast(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));

        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            var v = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
