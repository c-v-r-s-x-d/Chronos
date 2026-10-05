using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>
/// Sentences around a length of time, with hand-written expectations: comparing against the same
/// <c>Duration.Say</c> the screen uses would agree on a wrong form. Russian puts what follows «через»
/// in the accusative; only «минута» and «секунда» differ in the singular, «час» never does.
/// </summary>
[Collection(CultureBound.Name)]
public sealed class SentenceGrammarTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The waiting screen: «Блокировка снимется через 1 минуту», not «через 1 минута».</summary>
    [Theory]
    [InlineData(60, "Блокировка снимется через 1 минуту.")]
    [InlineData(21 * 60, "Блокировка снимется через 21 минуту.")]
    [InlineData(2 * 60, "Блокировка снимется через 2 минуты.")]
    [InlineData(5 * 60, "Блокировка снимется через 5 минут.")]
    [InlineData(11 * 60, "Блокировка снимется через 11 минут.")]
    [InlineData(1, "Блокировка снимется через 1 секунду.")]
    [InlineData(21, "Блокировка снимется через 21 секунду.")]
    [InlineData(2, "Блокировка снимется через 2 секунды.")]
    [InlineData(5, "Блокировка снимется через 5 секунд.")]
    // Both nouns follow «через», so both are accusative; the masculine hour is unchanged.
    [InlineData(101 * 60, "Блокировка снимется через 1 час 41 минуту.")]
    [InlineData(2 * 3600, "Блокировка снимется через 2 часа.")]
    public void TheWaitingScreenPutsTheDurationInTheAccusativeInRussian(int seconds, string expected) =>
        Assert.Equal(expected, Waiting("ru-RU", TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(60, "The block lifts in 1 minute.")]
    [InlineData(21 * 60, "The block lifts in 21 minutes.")]
    [InlineData(1, "The block lifts in 1 second.")]
    [InlineData(21, "The block lifts in 21 seconds.")]
    [InlineData(101 * 60, "The block lifts in 1 hour 41 minutes.")]
    public void TheWaitingScreenSaysTheSameThingInEnglish(int seconds, string expected) =>
        Assert.Equal(expected, Waiting("en-US", TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(1, "Снятие вступит в силу через 1 минуту после запроса.")]
    [InlineData(21, "Снятие вступит в силу через 21 минуту после запроса.")]
    [InlineData(30, "Снятие вступит в силу через 30 минут после запроса.")]
    [InlineData(101, "Снятие вступит в силу через 1 час 41 минуту после запроса.")]
    public void TheCoolDownNoticeIsInTheAccusativeInRussian(int minutes, string expected) =>
        Assert.Equal(expected, CoolDown("ru-RU", minutes));

    [Theory]
    [InlineData(1, "Lifting the block takes effect 1 minute after it is asked for.")]
    [InlineData(21, "Lifting the block takes effect 21 minutes after it is asked for.")]
    [InlineData(101, "Lifting the block takes effect 1 hour 41 minutes after it is asked for.")]
    public void TheCoolDownNoticeSaysTheSameThingInEnglish(int minutes, string expected) =>
        Assert.Equal(expected, CoolDown("en-US", minutes));

    /// <summary>«Осталось» takes the nominative, so this sentence must stay as it was.</summary>
    [Theory]
    [InlineData(60, "Осталось 1 минута.")]
    [InlineData(21 * 60, "Осталось 21 минута.")]
    [InlineData(1, "Осталось 1 секунда.")]
    [InlineData(21, "Осталось 21 секунда.")]
    [InlineData(101 * 60, "Осталось 1 час 41 минута.")]
    public void TheActiveScreenKeepsTheNominativeInRussian(int seconds, string expected) =>
        Assert.Equal(expected, Active("ru-RU", TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(60, "1 minute left.")]
    [InlineData(21 * 60, "21 minutes left.")]
    [InlineData(1, "1 second left.")]
    public void TheActiveScreenSaysTheSameThingInEnglish(int seconds, string expected) =>
        Assert.Equal(expected, Active("en-US", TimeSpan.FromSeconds(seconds)));

    private static string Waiting(string culture, TimeSpan left)
    {
        using var language = new UiCulture(culture);
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("UnlockPending", Moment, endsAt: Moment.AddHours(3), unlockAt: Moment + left));

        return Assert.IsType<WaitingViewModel>(app.Shell.CurrentScreen).RemainingText;
    }

    private static string Active(string culture, TimeSpan left)
    {
        using var language = new UiCulture(culture);
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment + left));

        return Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen).RemainingText;
    }

    private static string CoolDown(string culture, int minutes)
    {
        using var language = new UiCulture(culture);
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddHours(1), coolDownMinutes: minutes));

        return Assert.IsType<ActiveViewModel>(app.Shell.CurrentScreen).CoolDownNotice;
    }
}
