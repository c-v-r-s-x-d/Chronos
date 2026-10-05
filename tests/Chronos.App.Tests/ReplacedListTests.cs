using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>
/// A replaced list says its selection as none for one announcement. A listener that throws during
/// it must not leave the selection reading as none for good.
/// </summary>
[Collection(CultureBound.Name)]
public sealed class ReplacedListTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSidebarSelectionComesBackAfterAListenerThrew()
    {
        using var culture = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        app.Shell.Section = Section.Apps;
        ThrowOnce(app.Shell, nameof(ShellViewModel.Sections));

        Assert.Throws<InvalidOperationException>(() => app.Language.Use("ru-RU"));

        Assert.Equal((int)Section.Apps, app.Shell.SelectedSectionIndex);
    }

    [Fact]
    public void TheChipSelectionComesBackAfterAListenerThrew()
    {
        using var culture = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment));
        var setup = app.Shell.SetupScreen();
        setup.SelectedDuration = 2;
        ThrowOnce(setup, nameof(SetupViewModel.Durations));

        Assert.Throws<InvalidOperationException>(setup.Reword);

        Assert.Equal(2, setup.SelectedDuration);
    }

    private static void ThrowOnce(System.ComponentModel.INotifyPropertyChanged source, string property)
    {
        var thrown = false;
        source.PropertyChanged += (_, e) =>
        {
            if (!thrown && e.PropertyName == property)
            {
                thrown = true;
                throw new InvalidOperationException("a listener failed");
            }
        };
    }
}
