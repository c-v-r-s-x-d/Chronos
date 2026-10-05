using System.Globalization;
using Chronos.App.Localization;

namespace Chronos.App.Tests;

/// <summary>
/// The configuration's language arrives after an <c>await</c>. The culture lives in the execution
/// context, so a switch made there is undone when the async method returns; the interface then
/// shows one language in the words it built during the switch and another everywhere else.
/// </summary>
[Collection(CultureBound.Name)]
public sealed class LanguageSwitchFlowTests
{
    [Fact]
    public async Task ASwitchMadeInsideAnAsyncMethodDoesNotOutliveIt()
    {
        using var machine = new UiCulture("ru-RU");
        var language = new LanguageSwitch();

        await SwitchAfterAnAwait(language, "en");

        // This is the failure the queued switch exists for.
        Assert.Equal("ru-RU", CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public async Task AQueuedSwitchMadeInsideAnAsyncMethodStays()
    {
        using var machine = new UiCulture("ru-RU");
        var queue = new Queue<Action>();
        var language = new LanguageSwitch(post: queue.Enqueue);
        var announced = 0;
        language.Changed += (_, _) => announced++;

        await SwitchAfterAnAwait(language, "en");
        Assert.Equal(0, announced);

        // What the UI thread's dispatcher does next, outside any async method.
        queue.Dequeue()();

        Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
        Assert.Equal(1, announced);
        Assert.Equal("en", language.Current.Name);
    }

    private static async Task SwitchAfterAnAwait(LanguageSwitch language, string choice)
    {
        await Task.Yield();
        language.Choose(choice);
    }
}
