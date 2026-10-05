using System.Globalization;

namespace Chronos.App.Localization;

/// <summary>How long is left, in words; the unit is chosen in one place.</summary>
public static class Duration
{
    /// <summary>A span in words: seconds below a minute, minutes below an hour, then hours and minutes. Rounded up, so a countdown never sits on zero early.</summary>
    /// <param name="form">The case of the sentence this goes into; both nouns of "an hour and forty-one minutes" take it.</param>
    public static string Say(TimeSpan left, GrammaticalCase form = GrammaticalCase.Nominative)
    {
        if (left < TimeSpan.Zero)
        {
            left = TimeSpan.Zero;
        }

        if (left < TimeSpan.FromMinutes(1))
        {
            var seconds = (int)Math.Ceiling(left.TotalSeconds);

            return Unit(seconds, Plural.Seconds(seconds, form));
        }

        var minutes = (int)Math.Ceiling(left.TotalMinutes);

        if (minutes < 60)
        {
            return Unit(minutes, Plural.Minutes(minutes, form));
        }

        var hours = minutes / 60;
        var rest = minutes % 60;

        return rest == 0
            ? Unit(hours, Plural.Hours(hours, form))
            : Unit(hours, Plural.Hours(hours, form)) + " " + Unit(rest, Plural.Minutes(rest, form));
    }

    /// <summary>A span as clock digits: H:MM:SS from one hour up, M:SS below. Rounded up, as <see cref="Say"/> is.</summary>
    public static string Digits(TimeSpan left)
    {
        var seconds = left <= TimeSpan.Zero ? 0L : (long)Math.Ceiling(left.TotalSeconds);
        var hours = seconds / 3600;
        var minutes = seconds / 60 % 60;
        var rest = seconds % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>A count and its noun. The space between them is not a phrase worth a resource key.</summary>
    private static string Unit(int count, string noun) => string.Create(Screen, $"{count} {noun}");

    /// <summary>The screen's language decides how the number is written, not the machine's formatting culture, so logs stay machine-readable.</summary>
    private static CultureInfo Screen => CultureInfo.CurrentUICulture;
}
