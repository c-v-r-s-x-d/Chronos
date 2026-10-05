using Chronos.App.Localization;

namespace Chronos.App.Tests;

/// <summary>The forms come from the resource files; the rule that picks between them is code. Covers the teens and the hundreds that end in a teen.</summary>
[Collection(CultureBound.Name)]
public sealed class PluralTests
{
    [Theory]
    // The six values of the plural scenario, in its order.
    [InlineData(1, "минута")]
    [InlineData(2, "минуты")]
    [InlineData(5, "минут")]
    [InlineData(11, "минут")]
    [InlineData(21, "минута")]
    [InlineData(101, "минута")]
    // Zero is on every screen the moment a session ends.
    [InlineData(0, "минут")]
    // The teens: every one of them is "минут", including the ones whose last digit says otherwise.
    [InlineData(12, "минут")]
    [InlineData(13, "минут")]
    [InlineData(14, "минут")]
    // And the same teens a hundred up, where the rule is most often written wrong.
    [InlineData(111, "минут")]
    [InlineData(112, "минут")]
    [InlineData(113, "минут")]
    [InlineData(114, "минут")]
    // Either side of them, to show the exclusion is the teens and not the whole hundred.
    [InlineData(110, "минут")]
    [InlineData(115, "минут")]
    [InlineData(121, "минута")]
    [InlineData(122, "минуты")]
    [InlineData(124, "минуты")]
    [InlineData(125, "минут")]
    public void Minutes_PicksTheRussianForm(int value, string expected)
    {
        using var language = new UiCulture("ru-RU");

        Assert.Equal(expected, Plural.Minutes(value));
    }

    [Theory]
    [InlineData(1, "час")]
    [InlineData(2, "часа")]
    [InlineData(5, "часов")]
    [InlineData(11, "часов")]
    [InlineData(21, "час")]
    [InlineData(0, "часов")]
    [InlineData(12, "часов")]
    [InlineData(114, "часов")]
    public void Hours_PicksTheRussianForm(int value, string expected)
    {
        using var language = new UiCulture("ru-RU");

        Assert.Equal(expected, Plural.Hours(value));
    }

    [Theory]
    [InlineData(0, "minutes")]
    [InlineData(1, "minute")]
    [InlineData(2, "minutes")]
    [InlineData(11, "minutes")]
    [InlineData(21, "minutes")]
    [InlineData(101, "minutes")]
    public void Minutes_PicksTheEnglishForm(int value, string expected)
    {
        using var language = new UiCulture("en-US");

        Assert.Equal(expected, Plural.Minutes(value));
    }

    [Theory]
    [InlineData(1, "hour")]
    [InlineData(2, "hours")]
    [InlineData(21, "hours")]
    public void Hours_PicksTheEnglishForm(int value, string expected)
    {
        using var language = new UiCulture("en-US");

        Assert.Equal(expected, Plural.Hours(value));
    }

    /// <summary>A regional variant of Russian takes the Russian rule with the Russian forms.</summary>
    [Theory]
    [InlineData(21, "минута")]
    [InlineData(11, "минут")]
    public void ARegionalVariantOfRussianCountsInRussian(int value, string expected)
    {
        using var language = new UiCulture("ru-KZ");

        Assert.Equal(expected, Plural.Minutes(value));
    }

    /// <summary>The accusative after «через». Only the singular differs: the accusative of an inanimate feminine plural is its nominative.</summary>
    [Theory]
    [InlineData(1, "минуту")]
    [InlineData(21, "минуту")]
    [InlineData(101, "минуту")]
    [InlineData(2, "минуты")]
    [InlineData(5, "минут")]
    [InlineData(11, "минут")]
    [InlineData(111, "минут")]
    public void Minutes_PicksTheRussianAccusative(int value, string expected)
    {
        using var language = new UiCulture("ru-RU");

        Assert.Equal(expected, Plural.Minutes(value, GrammaticalCase.Accusative));
    }

    [Theory]
    [InlineData(1, "секунду")]
    [InlineData(21, "секунду")]
    [InlineData(101, "секунду")]
    [InlineData(2, "секунды")]
    [InlineData(5, "секунд")]
    public void Seconds_PicksTheRussianAccusative(int value, string expected)
    {
        using var language = new UiCulture("ru-RU");

        Assert.Equal(expected, Plural.Seconds(value, GrammaticalCase.Accusative));
    }

    /// <summary>«час» is masculine and inanimate, so its accusative is its nominative; asserted because it reads right by accident.</summary>
    [Theory]
    [InlineData(1, "час")]
    [InlineData(21, "час")]
    [InlineData(2, "часа")]
    [InlineData(5, "часов")]
    public void Hours_AreTheSameInBothCases(int value, string expected)
    {
        using var language = new UiCulture("ru-RU");

        Assert.Equal(expected, Plural.Hours(value, GrammaticalCase.Accusative));
        Assert.Equal(expected, Plural.Hours(value));
    }

    [Theory]
    [InlineData(1, "minute")]
    [InlineData(21, "minutes")]
    [InlineData(101, "minutes")]
    public void TheCaseChangesNothingInEnglish(int value, string expected)
    {
        using var language = new UiCulture("en-US");

        Assert.Equal(expected, Plural.Minutes(value, GrammaticalCase.Accusative));
        Assert.Equal(expected, Plural.Minutes(value));
    }

    /// <summary>A language with no translation falls back to the English resource and must use the English rule, or "2 minute" reaches the screen.</summary>
    [Theory]
    [InlineData(1, "minute")]
    [InlineData(21, "minutes")]
    public void ALanguageWithNoTranslationCountsInEnglish(int value, string expected)
    {
        using var language = new UiCulture("de-DE");

        Assert.Equal(expected, Plural.Minutes(value));
    }

    [Theory]
    [InlineData(1, "сайт")]
    [InlineData(3, "сайта")]
    [InlineData(12, "сайтов")]
    [InlineData(21, "сайт")]
    public void Sites_PicksTheRussianForm(int value, string expected)
    {
        using var language = new UiCulture("ru-RU");

        Assert.Equal(expected, Plural.Sites(value));
    }

    [Theory]
    [InlineData(1, "приложение")]
    [InlineData(3, "приложения")]
    [InlineData(5, "приложений")]
    [InlineData(11, "приложений")]
    public void Apps_PicksTheRussianForm(int value, string expected)
    {
        using var language = new UiCulture("ru-RU");

        Assert.Equal(expected, Plural.Apps(value));
    }

    [Theory]
    [InlineData(1, "site", "app")]
    [InlineData(2, "sites", "apps")]
    public void SitesAndApps_PickTheEnglishForm(int value, string site, string app)
    {
        using var language = new UiCulture("en-US");

        Assert.Equal(site, Plural.Sites(value));
        Assert.Equal(app, Plural.Apps(value));
    }
}
