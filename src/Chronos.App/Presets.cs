using Chronos.App.Resources;

namespace Chronos.App;

/// <summary>A built-in set of domains, grouped by the kind of service. The identifier is what the code and the resources agree on.</summary>
public sealed record Preset(string Id, IReadOnlyList<string> Domains);

/// <summary>A preset with its name in the screen's language, rebuilt on each redraw so a language change reaches it.</summary>
public sealed record PresetLine(Preset Preset, string Name);

/// <summary>The lists that come with the product. Applying one copies its domains into the user's list as ordinary rules; there is no link back to the preset.</summary>
public static class Presets
{
    public const string Social = "social";

    public const string Video = "video";

    public const string News = "news";

    public const string Games = "games";

    public static IReadOnlyList<Preset> All { get; } =
    [
        new(
            Social,
            [
                "facebook.com",
                "instagram.com",
                "linkedin.com",
                "ok.ru",
                "reddit.com",
                "snapchat.com",
                "tiktok.com",
                "twitter.com",
                "vk.com",
                "x.com",
            ]),
        new(
            Video,
            [
                "dailymotion.com",
                "kinopoisk.ru",
                "netflix.com",
                "rutube.ru",
                "twitch.tv",
                "vimeo.com",
                "youtube.com",
            ]),
        new(
            News,
            [
                "bbc.com",
                "cnn.com",
                "lenta.ru",
                "news.ycombinator.com",
                "rbc.ru",
                "ria.ru",
            ]),
        new(
            Games,
            [
                "battle.net",
                "epicgames.com",
                "gog.com",
                "itch.io",
                "roblox.com",
                "steampowered.com",
            ]),
    ];

    /// <summary>The preset's name in the screen's language, read at the moment of asking. An unknown identifier is shown as itself.</summary>
    public static string Name(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        return preset.Id switch
        {
            Social => Strings.PresetSocial,
            Video => Strings.PresetVideo,
            News => Strings.PresetNews,
            Games => Strings.PresetGames,
            _ => preset.Id,
        };
    }
}
