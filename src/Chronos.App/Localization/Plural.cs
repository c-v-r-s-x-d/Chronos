using System.Globalization;
using Chronos.App.Resources;

namespace Chronos.App.Localization;

/// <summary>Picks the noun form for a count. The forms are resources; the rule is code.</summary>
public static class Plural
{
    public static string Minutes(int count, GrammaticalCase form = GrammaticalCase.Nominative) =>
        Say(count, One(Strings.MinuteOne, Strings.MinuteOneAccusative, form), Strings.MinuteFew, Strings.MinuteMany);

    public static string Hours(int count, GrammaticalCase form = GrammaticalCase.Nominative) =>
        Say(count, One(Strings.HourOne, Strings.HourOneAccusative, form), Strings.HourFew, Strings.HourMany);

    public static string Sites(int count) => Say(count, Strings.SiteOne, Strings.SiteFew, Strings.SiteMany);

    public static string Apps(int count) => Say(count, Strings.AppOne, Strings.AppFew, Strings.AppMany);

    /// <summary>Seconds, for the last minute of a countdown.</summary>
    public static string Seconds(int count, GrammaticalCase form = GrammaticalCase.Nominative) =>
        Say(count, One(Strings.SecondOne, Strings.SecondOneAccusative, form), Strings.SecondFew, Strings.SecondMany);

    /// <summary>
    /// The singular in the case the sentence asks for. Russian shows the case only in the singular
    /// («2 минуты» and «5 минут» read the same after «через»), so few and many have no accusative
    /// key. A language that declined the plural would need more keys.
    /// </summary>
    private static string One(string nominative, string accusative, GrammaticalCase form) =>
        form == GrammaticalCase.Accusative ? accusative : nominative;

    private static string Say(int count, string one, string few, string many) => Which(count) switch
    {
        Form.One => one,
        Form.Few => few,
        _ => many,
    };

    /// <summary>Follows the language the resources answered in, not the machine's: a culture with no translation gets English strings, and Russian rules on those give "2 minute".</summary>
    private static Form Which(int count)
    {
        var n = Math.Abs((long)count);

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? Russian(n)
            : English(n);
    }

    private static Form English(long n) => n == 1 ? Form.One : Form.Many;

    // 1, 21, 101 - one; 2..4, 22..24 - few; everything else, and the whole of 11..14 whatever
    // their last digit says, - many.
    private static Form Russian(long n) => (n % 10, n % 100) switch
    {
        (1, not 11) => Form.One,
        (>= 2 and <= 4, < 12 or > 14) => Form.Few,
        _ => Form.Many,
    };

    private enum Form
    {
        One,
        Few,
        Many,
    }
}
