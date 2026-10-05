using System.Text.RegularExpressions;

namespace Chronos.App.Tests;

/// <summary>Keeps style selectors from drifting back to classes nothing sets or to Fluent's defaults. The window is the real check.</summary>
public sealed class StyleMarkupTests
{
    private static string Styles => File.ReadAllText(Repo.App("Theme", "Styles.axaml"));

    private static string Markup(params string[] parts) => File.ReadAllText(Repo.App(parts));

    // The setters of the one style with exactly this selector.
    private static string Style(string selector)
    {
        var match = Regex.Match(
            Styles,
            $@"<Style Selector=""{Regex.Escape(selector)}"">(?<body>.*?)</Style>",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"No style for {selector}.");

        return match.Groups["body"].Value;
    }

    private static void Sets(string body, string property, string value) =>
        Assert.Contains($@"<Setter Property=""{property}"" Value=""{value}"" />", body);

    [Fact]
    public void NoStyleSelectsAChipClassThatNoItemCarries()
    {
        Assert.DoesNotContain("ListBoxItem.chip", Styles);
        Assert.DoesNotContain(@"Classes=""chip""", Markup("Views", "SessionSetupPage.axaml").Replace(@"<ListBox Classes=""chips""", string.Empty));
    }

    [Fact]
    public void TheDurationChipsAreSeparateAndFramed()
    {
        var chip = Style("ListBox.chips ListBoxItem");

        Sets(chip, "BorderBrush", "{DynamicResource Chronos.Border}");
        Sets(chip, "BorderThickness", "1");
        Assert.Matches(@"<Setter Property=""Margin"" Value=""0,0,\d+,\d+"" />", chip);
    }

    [Fact]
    public void ASelectedChipIsGoldWithItsTextOnTheAccent()
    {
        var selected = Style("ListBox.chips ListBoxItem:selected /template/ ContentPresenter#PART_ContentPresenter");

        Sets(selected, "Background", "{DynamicResource Chronos.Accent}");
        Sets(selected, "Foreground", "{DynamicResource Chronos.OnAccent}");
        Style("ListBox.chips ListBoxItem:pointerover /template/ ContentPresenter#PART_ContentPresenter");
    }

    [Fact]
    public void TheExtendButtonsAreChipsAndNotFluentsGreyButtons()
    {
        var chip = Style("ToggleButton.chip, Button.chip");
        Sets(chip, "Background", "Transparent");
        Sets(chip, "BorderBrush", "{DynamicResource Chronos.Border}");

        Sets(Style("ToggleButton.chip:pointerover /template/ ContentPresenter#PART_ContentPresenter, Button.chip:pointerover /template/ ContentPresenter#PART_ContentPresenter"),
            "BorderBrush", "{DynamicResource Chronos.Accent}");
        Sets(Style("ToggleButton.chip:checked /template/ ContentPresenter#PART_ContentPresenter"),
            "Background", "{DynamicResource Chronos.Accent}");

        var page = Markup("Views", "SessionActivePage.axaml");
        Assert.Matches(@"<Button Classes=""chip""[^>]*ExtendByCommand", page);
        Assert.Matches(@"<ToggleButton Classes=""chip""[^>]*IsCustomExtendOpen", page);
    }

    [Fact]
    public void TheChipsAreLaidOutOnceAndInTheStyle()
    {
        Assert.DoesNotContain("<ListBox.ItemsPanel>", Markup("Views", "SessionSetupPage.axaml"));
        Assert.Contains("<WrapPanel />", Style("ListBox.chips"));
    }

    [Fact]
    public void APickedRunningProgramIsOnTheAccentWithAReadablePath()
    {
        Sets(Style("ListBox.running"), "Background", "{DynamicResource Chronos.Surface}");

        var selected = Style("ListBox.running ListBoxItem:selected /template/ ContentPresenter#PART_ContentPresenter");
        Sets(selected, "Background", "{DynamicResource Chronos.Accent}");
        Sets(selected, "Foreground", "{DynamicResource Chronos.OnAccent}");

        Sets(Style("ListBox.running ListBoxItem:selected TextBlock.muted"), "Foreground", "{DynamicResource Chronos.OnAccent}");
    }

    [Fact]
    public void TheDialogsTabsAreAtTheSizeOfTheRestOfTheText()
    {
        Assert.Matches(@"<TabControl[^>]*Classes=""tabs""", Markup("Views", "AddAppDialog.axaml"));

        var tab = Style("TabControl.tabs TabItem");
        var size = int.Parse(Regex.Match(tab, @"Property=""FontSize"" Value=""(?<v>\d+)""").Groups["v"].Value);

        Assert.InRange(size, 14, 15);
    }

    [Fact]
    public void ARemoveButtonIsQuietUntilItIsPointedAt()
    {
        var remove = Style("Button.remove");
        Sets(remove, "Background", "Transparent");
        Sets(remove, "BorderThickness", "0");

        Sets(Style("Button.remove PathIcon.cross"), "Foreground", "{DynamicResource Chronos.Muted}");
        Sets(Style("Button.remove:pointerover PathIcon.cross, Button.remove:focus-visible PathIcon.cross"), "Foreground", "{DynamicResource Chronos.Text}");

        // Off during a session, and the padlock is drawn in its place.
        Sets(Style("Button.remove:disabled"), "Opacity", "0");
    }

    [Fact]
    public void TheDialogAndTheBlockWindowCarryTheApplicationIcon()
    {
        Assert.Matches(@"new AddAppDialog\s*\{[^}]*Icon = owner\.Icon", Markup("Views", "AppsPage.axaml.cs"));

        var app = Markup("App.axaml.cs");
        Assert.Matches(@"new BlockScreen\s*\{[^}]*Icon = Icon\(\)", app);
        Assert.Matches(@"new MainWindow\s*\{[^}]*Icon = Icon\(\)", app);
        Assert.Contains(@"avares://Chronos.App/Assets/chronos.ico", app);
    }
}
