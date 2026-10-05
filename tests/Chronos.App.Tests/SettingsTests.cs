using System.Text.RegularExpressions;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.ViewModels;

namespace Chronos.App.Tests;

/// <summary>The settings section: what this machine can block, its summary in the sidebar, and the one promise the product refuses to make.</summary>
[Collection(CultureBound.Name)]
public sealed class SettingsTests
{
    private static readonly DateTimeOffset Moment = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A layer no pass has reported on is absent from the status and stays absent; unknown is not working.</summary>
    [Fact]
    public void OnlyTheLayersTheServiceReportedOnAreOnTheScreen()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment, layers: [Say.Layer("hosts")]));

        var only = Assert.Single(app.Shell.Settings.Layers);
        Assert.Equal(LayerReason.Describe(Say.Layer("hosts"), blocking: false).Name, only.Name);
    }

    /// <summary>No pass has reported on anything: nothing is claimed, neither working layers nor an empty list read as "nothing works".</summary>
    [Fact]
    public void WithNoLayerReportedOnNothingIsInventedAndTheScreenSaysSo()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment));

        Assert.Empty(app.Shell.Settings.Layers);
        Assert.False(app.Shell.Settings.AnyLayerReported);
    }

    [Fact]
    public void AnUnavailableLayerIsOnTheScreenWithItsReasonInWords()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(
            "Idle",
            Moment,
            layers: [Say.Layer("wfp", available: false, reason: "wfp.engine-unavailable")]));

        var line = Assert.Single(app.Shell.Settings.Layers);

        Assert.False(line.IsAvailable);
        Assert.Equal(LayerReason.Text("wfp.engine-unavailable"), line.Reason);
        Assert.NotEqual("wfp.engine-unavailable", line.Reason);
    }

    /// <summary>Layers are read afresh from every status, so a layer starting or stopping changes the screen.</summary>
    [Fact]
    public void TheLayersFollowTheStatusRatherThanTheFirstOneEverSeen()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment, layers: [Say.Layer("wfp", available: false, reason: "wfp.disabled-by-user")]));
        Assert.False(Assert.Single(app.Shell.Settings.Layers).IsAvailable);

        app.Show(Say.Status("Idle", Moment, layers: [Say.Layer("wfp"), Say.Layer("hosts")]));

        Assert.Equal(2, app.Shell.Settings.Layers.Count);
        Assert.All(app.Shell.Settings.Layers, line => Assert.True(line.IsAvailable));
    }

    /// <summary>Notices about sites are promised only while the DNS layer, the one that sees an attempt, is reported working.</summary>
    [Fact]
    public void NoticesAboutSitesArePromisedWhileTheDnsLayerIsWorking()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment, layers: [Say.Layer("dns")]));

        Assert.True(app.Shell.Settings.SiteNoticesAvailable);
    }

    [Fact]
    public void NoticesAboutSitesAreNotPromisedWhileTheDnsLayerIsUnavailable()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(
            "Idle", Moment, layers: [Say.Layer("hosts"), Say.Layer("dns", available: false, reason: "dns.port-busy")]));

        Assert.False(app.Shell.Settings.SiteNoticesAvailable);
    }

    [Fact]
    public void NoticesAboutSitesAreNotPromisedWhileTheDnsLayerHasNotReported()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment, layers: [Say.Layer("hosts"), Say.Layer("wfp")]));

        Assert.False(app.Shell.Settings.SiteNoticesAvailable);
    }

    [Fact]
    public void NoticesAboutSitesFollowTheDnsLayerDuringASessionToo()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Active", Moment, endsAt: Moment.AddHours(1), layers: [Say.Layer("dns")]));

        Assert.True(app.Shell.Settings.SiteNoticesAvailable);
    }

    [Fact]
    public void ThePromiseFollowsTheDnsLayerAndSaysThatItChanged()
    {
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment, layers: [Say.Layer("dns")]));
        var settings = app.Shell.Settings;
        var announced = new List<string>();
        settings.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        app.Show(Say.Status("Idle", Moment + TimeSpan.FromSeconds(15), layers: [Say.Layer("dns", available: false, reason: "dns.port-busy")]));

        Assert.False(settings.SiteNoticesAvailable);
        Assert.Contains(nameof(SettingsViewModel.SiteNoticesAvailable), announced);
    }

    /// <summary>The explanation names the layer it depends on and stays true before the first pass and when nothing works.</summary>
    [Theory]
    [InlineData("", "not working or has not been checked yet")]
    [InlineData("ru", "не работает или ещё не проверен")]
    public void TheExplanationNamesTheDnsResolverAndClaimsNoMoreThanIsKnown(string culture, string hedge)
    {
        var said = Chronos.App.Resources.Strings.ResourceManager.GetString(
            "SiteNoticesUnavailable", System.Globalization.CultureInfo.GetCultureInfo(culture));

        Assert.NotNull(said);
        Assert.Contains("DNS", said, StringComparison.Ordinal);
        Assert.Contains(hedge, said, StringComparison.Ordinal);
    }

    /// <summary>The explanation is on the screen exactly when the promise is not, bound to the layer's state.</summary>
    [Fact]
    public void TheScreenExplainsTheMissingNoticesOnlyWhenTheyAreMissing()
    {
        using var app = new AppUnderTest(Moment);

        Assert.False(string.IsNullOrWhiteSpace(app.Text.SiteNoticesUnavailable));

        var markup = File.ReadAllText(Repo.App("Views", "SettingsPage.axaml"));

        Assert.Matches(
            new Regex(
                @"<TextBlock\s+Text=""\{Binding\s+Text\.SiteNoticesUnavailable\}""[^>]*IsVisible=""\{Binding\s+!SiteNoticesAvailable\}""",
                RegexOptions.None),
            markup);
    }

    /// <summary>The layer lines are built by this screen, not named by markup, so nothing refreshes them unless told.</summary>
    [Fact]
    public void ChangingTheLanguageChangesTheWordsOnTheLayerLines()
    {
        using var restore = new UiCulture("en-US");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status("Idle", Moment, layers: [Say.Layer("wfp", available: false, reason: "wfp.engine-unavailable")]));

        var english = Assert.Single(app.Shell.Settings.Layers).Reason;

        app.Language.Use("ru-RU");

        Assert.NotEqual(english, Assert.Single(app.Shell.Settings.Layers).Reason);
    }

    [Theory]
    [InlineData(true, ProtectionState.Off)]
    [InlineData(false, ProtectionState.OffWithProblem)]
    public void WithNoSessionTheSummarySaysProtectionIsOff(bool dnsWorks, ProtectionState expected)
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(EngineState.Idle, Moment, layers:
            [Say.Layer("hosts"), Say.Layer("dns", available: dnsWorks, reason: dnsWorks ? null : "dns.port-busy")]));

        Assert.Equal("Защита выключена", app.Shell.Settings.ProtectionSummary);
        Assert.Equal(expected, app.Shell.Settings.Protection);
        Assert.False(app.Shell.Settings.ProtectionOn);
        Assert.Equal(!dnsWorks, app.Shell.Settings.ProtectionWarning);
        Assert.Equal(dnsWorks, app.Shell.Settings.ProtectionNeutral);
    }

    [Theory]
    [InlineData(true, "Защита включена", ProtectionState.On)]
    [InlineData(false, "Защита включена частично", ProtectionState.Partial)]
    public void DuringASessionTheSummarySaysProtectionIsOn(bool dnsWorks, string summary, ProtectionState expected)
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1), layers:
            [Say.Layer("hosts"), Say.Layer("dns", available: dnsWorks, reason: dnsWorks ? null : "dns.port-busy")]));

        Assert.Equal(summary, app.Shell.Settings.ProtectionSummary);
        Assert.Equal(expected, app.Shell.Settings.Protection);
        Assert.Equal(dnsWorks, app.Shell.Settings.ProtectionOn);
        Assert.Equal(!dnsWorks, app.Shell.Settings.ProtectionWarning);
        Assert.False(app.Shell.Settings.ProtectionNeutral);
    }

    [Fact]
    public void WithNothingReportedTheSummarySaysItDoesNotKnow()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1)));

        Assert.Equal(ProtectionState.Unknown, app.Shell.Settings.Protection);
        Assert.Equal("Состояние неизвестно", app.Shell.Settings.ProtectionSummary);
        Assert.True(app.Shell.Settings.ProtectionNeutral);
    }

    [Fact]
    public void NoSummaryCountsMechanisms()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1), layers: [Say.Layer("hosts"), Say.Layer("wfp")]));

        Assert.DoesNotMatch(@"\d", app.Shell.Settings.ProtectionSummary);
    }

    [Fact]
    public void TheSummaryFollowsTheLanguage()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment, layers: [Say.Layer("hosts")]));

        app.Text.SwitchTo(LanguageChoice.English);

        Assert.Equal("Protection off", app.Shell.Settings.ProtectionSummary);
    }

    [Fact]
    public void StartingASessionTurnsTheSummaryAndTheWordsOn()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment, layers: [Say.Layer("hosts")]));
        Assert.Equal("Способы блокировки", app.Shell.Settings.ProtectionHeading);
        Assert.Equal("доступен", app.Shell.Settings.Layers.Single().State);
        var announced = new List<string>();
        app.Shell.Settings.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        app.Show(Say.Status(EngineState.Active, Moment, endsAt: Moment.AddHours(1), layers: [Say.Layer("hosts")]));

        Assert.Equal("Защита", app.Shell.Settings.ProtectionHeading);
        Assert.Equal("работает", app.Shell.Settings.Layers.Single().State);
        Assert.Contains(nameof(SettingsViewModel.ProtectionSummary), announced);
        Assert.Contains(nameof(SettingsViewModel.ProtectionHeading), announced);
        Assert.Contains(nameof(SettingsViewModel.ProtectionOn), announced);
        Assert.Contains(nameof(SettingsViewModel.ProtectionNeutral), announced);
        Assert.Contains(nameof(SettingsViewModel.ProtectionWarning), announced);
        Assert.Contains(nameof(SettingsViewModel.Layers), announced);
    }

    [Theory]
    [InlineData(EngineState.Idle, "недоступен")]
    [InlineData(EngineState.Active, "не работает")]
    public void AFailingMechanismIsWordedForTheState(string state, string word)
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);

        app.Show(Say.Status(state, Moment, endsAt: Moment.AddHours(1), layers: [Say.Layer("dns", available: false, reason: "dns.port-busy")]));

        Assert.Equal(word, app.Shell.Settings.Layers.Single().State);
    }

    [Fact]
    public void TheLayersAreNamedInWordsAndNotByTheirIdentifiers()
    {
        using var culture = new UiCulture("ru-RU");
        using var app = new AppUnderTest(Moment);
        app.Show(Say.Status(EngineState.Idle, Moment, layers: [Say.Layer("hosts"), Say.Layer("wfp")]));

        Assert.Equal(["Файл hosts", "Брандмауэр"], app.Shell.Settings.Layers.Select(l => l.Name));
    }

    [Fact]
    public void TheLayersAreNamedInEnglishToo()
    {
        using var culture = new UiCulture("en-US");

        Assert.Equal(
            ["Hosts file", "DNS resolver", "Firewall", "Closing apps"],
            new[] { "hosts", "dns", "wfp", "apps" }.Select(LayerReason.Name));
    }

    /// <summary>The version on the settings page is the build's, as the package and the programs carry it.</summary>
    [Fact]
    public void TheSettingsShowTheVersionOfThisBuild()
    {
        using var app = new AppUnderTest(Moment);
        var built = typeof(SettingsViewModel).Assembly.GetName().Version!;

        Assert.Equal($"{built.Major}.{built.Minor}.{built.Build}", app.Shell.Settings.Version);
        Assert.DoesNotContain("+", app.Shell.Settings.Version, StringComparison.Ordinal);

        var page = File.ReadAllText(Repo.App("Views", "SettingsPage.axaml"));
        Assert.Matches(@"\{Binding\s+Text\.VersionLabel\}", page);
        Assert.Matches(@"\{Binding\s+Version\}", page);
    }
}
