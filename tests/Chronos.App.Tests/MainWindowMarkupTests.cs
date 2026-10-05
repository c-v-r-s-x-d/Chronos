using System.Text.RegularExpressions;
using Chronos.App.Services;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>What cannot be asked of a view model: window sizes, the sidebar summary's target, the Enter key, fonts, the finishing state's controls and the hourglass transition. Read off the markup.</summary>
public sealed class MainWindowMarkupTests
{
    private static string Markup(params string[] parts) => File.ReadAllText(Repo.App(parts));

    private static string Root(string markup, string attribute) =>
        Regex.Match(markup, $@"\b{attribute}=""(?<v>[^""]+)""").Groups["v"].Value;

    [Fact]
    public void TheWindowHasTheSizesOfAppendixA()
    {
        var window = Markup("MainWindow.axaml");

        Assert.Equal("880", Root(window, "Width"));
        Assert.Equal("600", Root(window, "Height"));
        Assert.Equal("760", Root(window, "MinWidth"));
        Assert.Equal("520", Root(window, "MinHeight"));
        Assert.Matches(@"DockPanel\.Dock=""Left""\s+Width=""200""", window);
        Assert.Matches(@"IsVisible=""\{Binding ShowsSidebar\}""", window);
    }

    [Fact]
    public void TheSidebarSummaryOpensTheSettingsSection()
    {
        var window = Markup("MainWindow.axaml");

        Assert.Matches(
            @"Classes=""summary""[^>]*Command=""\{Binding GoToCommand\}""\s+CommandParameter=""\{x:Static vm:Section\.Settings\}""",
            window);

        using var app = new AppUnderTest(new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero));
        app.Show(Say.Status(EngineState.Idle, new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero)));
        app.Shell.GoToCommand.Execute(Section.Settings);

        Assert.Equal(Section.Settings, app.Shell.Section);
    }

    [Fact]
    public void EnterAddsADomainThroughTheSameCommandAsTheButton()
    {
        var sites = Markup("Views", "SitesPage.axaml");

        Assert.Matches(@"<KeyBinding Gesture=""Enter"" Command=""\{Binding Rules\.AddSiteCommand\}""", sites);
        Assert.Matches(@"Command=""\{Binding Rules\.AddSiteCommand\}""\s*/>", sites);
    }

    [Fact]
    public void HeadingsAndCountdownsAreSetInForumAndTheLogoInCinzel()
    {
        var styles = Markup("Theme", "Styles.axaml");

        Assert.Matches(@"TextBlock\.h1""[^<]*<Setter Property=""FontFamily"" Value=""\{DynamicResource Chronos\.HeadingFont\}""", styles);
        Assert.Matches(@"TextBlock\.timer""[^<]*<Setter Property=""FontFamily"" Value=""\{DynamicResource Chronos\.HeadingFont\}""", styles);
        Assert.Matches(@"TextBlock\.logo""[^<]*<Setter Property=""FontFamily"" Value=""\{DynamicResource Chronos\.LogoFont\}""", styles);

        foreach (var page in new[] { "SessionSetupPage", "SitesPage", "AppsPage", "SettingsPage", "SessionActivePage", "WaitingView" })
        {
            Assert.Contains(@"Classes=""h1""", Markup("Views", page + ".axaml"));
        }

        Assert.Contains(@"Classes=""timer""", Markup("Views", "SessionActivePage.axaml"));
        Assert.Contains(@"Classes=""timer""", Markup("Views", "WaitingView.axaml"));
        Assert.Contains(@"Classes=""logo""", Markup("MainWindow.axaml"));

        // Everything else is the system font: no other markup names one.
        foreach (var file in Repo.Markup().Where(f => !f.EndsWith("Palette.axaml") && !f.EndsWith("Styles.axaml")))
        {
            Assert.DoesNotContain("FontFamily", File.ReadAllText(file));
        }
    }

    [Fact]
    public void AFinishingSessionOffersNeitherExtensionNorUnlock()
    {
        var page = Markup("Views", "SessionActivePage.axaml");
        var gate = page.IndexOf(@"IsVisible=""{Binding !IsFinishing}""", StringComparison.Ordinal);

        Assert.True(gate > 0, "The extension and unlock controls are not behind !IsFinishing.");
        var before = page[..gate];
        var behind = page[gate..];

        foreach (var control in new[] { "ExtendByCommand", "ExtendCommand", "RequestUnlockCommand" })
        {
            Assert.DoesNotContain(control, before);
            Assert.Contains(control, behind);
        }

        Assert.Matches(@"SessionFinishing\}""[^>]*IsVisible=""\{Binding IsFinishing\}""", page);
    }

    [Fact]
    public void TheHourglassMovesItsSandWithATransitionRatherThanAJumpEverySecond()
    {
        var styles = Markup("Theme", "Styles.axaml");

        Assert.Matches(@"<DoubleTransition Property=""Progress"" Duration=""0:0:1""", styles);
    }
}
