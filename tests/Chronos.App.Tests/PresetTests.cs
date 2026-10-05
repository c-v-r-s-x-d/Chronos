using Chronos.App;
using Chronos.App.Localization;
using Chronos.App.Services;
using Chronos.App.Time;
using Chronos.App.ViewModels;
using Chronos.Ipc;

namespace Chronos.App.Tests;

/// <summary>A preset only fills a list; once applied it is the user's list, editable like any other.</summary>
[Collection(CultureBound.Name)]
public sealed class PresetTests : IAsyncLifetime
{
    // The list starts empty, so the nine left at the end are the scenario's.
    private readonly ServiceHarness _service = new() { Seed = [] };

    private readonly RecordedWaits _waits = new();

    private readonly LanguageSwitch _language = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _service.DisposeAsync().AsTask();

    /// <summary>Against a real service and configuration file: apply ten domains, remove one, and a freshly built screen shows the same nine.</summary>
    [Fact]
    public async Task APresetOfTenDomainsMinusOneIsNineDomainsThatAreStillThereWhenTheSettingsAreOpenedAgain()
    {
        _service.Start();

        await using var link = new ServiceLink(_service.PipeName, _waits.NoWaitAsync);
        link.Start();

        var snapshot = await LinkWait.ForAsync(
            link, static s => s.State == ServiceConnectionState.Available, "reached the service");

        var preset = Presets.All.Single(candidate => candidate.Id == Presets.Social);
        Assert.Equal(10, preset.Domains.Count);

        var screen = await OpenAsync(link, snapshot.Status!);
        Assert.Empty(screen.Sites);

        await screen.ApplyPresetCommand.ExecuteAsync(preset);
        Assert.Equal(10, screen.Sites.Count);

        await screen.RemoveSiteCommand.ExecuteAsync(screen.Sites.Single(site => site.Domain == preset.Domains[3]));

        var nine = screen.Sites.Select(site => site.Domain).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(9, nine.Length);
        Assert.DoesNotContain(preset.Domains[3], nine);

        var again = await OpenAsync(link, snapshot.Status!);

        Assert.Equal(nine, again.Sites.Select(site => site.Domain).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Applying a preset is a run of AddSiteRule commands; the interface has no way to write the configuration itself.</summary>
    [Fact]
    public async Task ApplyingAPresetIsNothingButAddSiteRuleCommands()
    {
        var link = new RecordingLink();
        var preset = Presets.All.Single(candidate => candidate.Id == Presets.Social);

        var text = new Text(_language);
        using RuleScreenViewModel screen = new SetupViewModel(link, text, new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));

        await screen.ApplyPresetCommand.ExecuteAsync(preset);

        Assert.Equal(preset.Domains.Count, link.Sent.Count);
        Assert.All(link.Sent, request => Assert.Equal("AddSiteRule", request.Command));
        Assert.Equal(
            preset.Domains.Order(StringComparer.Ordinal).ToArray(),
            link.Sent.Select(request => request.Site!.Domain).Order(StringComparer.Ordinal).ToArray());

        // Subdomains included: a block that let through "m." and "www." would be a preset that does not work.
        Assert.All(link.Sent, request => Assert.True(request.Site!.IncludeSubdomains));
    }

    [Fact]
    public void ThePresetsAreGroupedByKindOfServiceAndEachOneHasDomains()
    {
        Assert.NotEmpty(Presets.All);
        Assert.Equal(Presets.All.Count, Presets.All.Select(preset => preset.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var preset in Presets.All)
        {
            Assert.NotEmpty(preset.Domains);
            Assert.Equal(
                preset.Domains.Count,
                preset.Domains.Distinct(StringComparer.Ordinal).Count());

            foreach (var domain in preset.Domains)
            {
                Assert.Equal(domain.ToLowerInvariant(), domain);
                Assert.Contains('.', domain);
            }
        }
    }

    [Fact]
    public void EveryPresetIsNamedInBothLanguages()
    {
        foreach (var preset in Presets.All)
        {
            string english;
            using (var _ = new UiCulture("en-US"))
            {
                english = Presets.Name(preset);
            }

            using var russian = new UiCulture("ru-RU");

            Assert.False(string.IsNullOrWhiteSpace(english), $"{preset.Id} has no name.");
            Assert.NotEqual(preset.Id, english);
            Assert.NotEqual(english, Presets.Name(preset));
        }
    }

    [Fact]
    public void TheScreenOffersEveryPresetByName()
    {
        using var restore = new UiCulture("en-US");

        var link = new RecordingLink();
        var text = new Text(_language);
        using RuleScreenViewModel screen = new SetupViewModel(link, text, new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));

        Assert.Equal(
            Presets.All.Select(preset => preset.Id).ToArray(),
            screen.Presets.Select(line => line.Preset.Id).ToArray());

        var first = screen.Presets[0];
        Assert.Equal(Presets.Name(first.Preset), first.Name);

        _language.Use("ru-RU");

        Assert.NotEqual(first.Name, screen.Presets[0].Name);
    }

    private async Task<RuleScreenViewModel> OpenAsync(IServiceLink link, StatusPayload status)
    {
        RuleScreenViewModel screen = new SetupViewModel(link, new Text(_language), new ServiceClock(new FakeClock(DateTimeOffset.UnixEpoch)));
        screen.Show(status);
        await screen.Loading;

        return screen;
    }
}
