using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Chronos.App.Tests;

/// <summary>
/// The palettes are the Fluent theme's, and following the system setting is Avalonia's own doing
/// while the variant is Default (measured: WM_SETTINGCHANGE "ImmersiveColorSet" after flipping
/// AppsUseLightTheme moved ActualThemeVariant). These tests guard against a pinned variant, which
/// builds and looks fine but stops following.
/// </summary>
public sealed class ThemeTests
{
    [Fact]
    public void TheApplicationAsksForTheSystemsOwnVariantRatherThanPinningOne()
    {
        var requested = Variant(File.ReadAllText(Repo.App("App.axaml")));

        Assert.Equal("Default", requested);
    }

    /// <summary>Proves the reader can fail: given markup that pins a variant it says which one.</summary>
    [Fact]
    public void TheReaderWouldNoticeAPinnedVariant()
    {
        Assert.Equal("Light", Variant("<Application RequestedThemeVariant=\"Light\" />"));
        Assert.Equal("Dark", Variant("<Application RequestedThemeVariant=\"Dark\" />"));
        Assert.Null(Variant("<Application />"));
    }

    /// <summary>The variant is inherited, so one window or control asking for its own would stop following unnoticed.</summary>
    [Fact]
    public void NothingBelowTheApplicationPinsAVariantOfItsOwn()
    {
        var application = Repo.App("App.axaml");

        foreach (var file in Repo.Markup().Where(file => !string.Equals(file, application, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Null(Variant(File.ReadAllText(file)));
        }
    }

    /// <summary>The Fluent theme carries both palettes; without it Default resolves to a variant no resource is keyed for.</summary>
    [Fact]
    public void TheApplicationCarriesAThemeThatHasBothPalettes()
    {
        var markup = File.ReadAllText(Repo.App("App.axaml"));

        Assert.Contains("<FluentTheme", markup, StringComparison.Ordinal);
    }

    /// <summary>Names the members the following rests on, so a rename is a compile error. The platform is reached through an interface, off the application.</summary>
    [Fact]
    public void TheAvaloniaMembersTheFollowingRestsOnAreThere()
    {
        var settings = typeof(Application).GetProperty(nameof(Application.PlatformSettings));

        Assert.NotNull(settings);
        Assert.Equal(typeof(IPlatformSettings), settings.PropertyType);
        Assert.NotNull(typeof(IPlatformSettings).GetEvent(nameof(IPlatformSettings.ColorValuesChanged)));
        Assert.NotNull(typeof(IPlatformSettings).GetMethod(nameof(IPlatformSettings.GetColorValues)));
        Assert.NotNull(typeof(Application).GetProperty(nameof(Application.ActualThemeVariant)));
    }

    /// <summary>The platform's answer maps onto the light and dark palettes as the framework says.</summary>
    [Fact]
    public void ThePlatformsAnswerMapsOntoTheTwoPalettes()
    {
        Assert.NotEqual(ThemeVariant.Light, ThemeVariant.Dark);
        Assert.Equal(ThemeVariant.Light, (ThemeVariant)PlatformThemeVariant.Light);
        Assert.Equal(ThemeVariant.Dark, (ThemeVariant)PlatformThemeVariant.Dark);

        // Default is neither: it is the request to be told, which is the whole mechanism.
        Assert.NotEqual(ThemeVariant.Default, ThemeVariant.Light);
        Assert.NotEqual(ThemeVariant.Default, ThemeVariant.Dark);
    }

    private static string? Variant(string markup)
    {
        var match = Regex.Match(markup, @"RequestedThemeVariant\s*=\s*""(?<value>[^""]*)""");

        return match.Success ? match.Groups["value"].Value : null;
    }
}
