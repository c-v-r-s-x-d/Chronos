using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Chronos.App.Localization;
using Chronos.App.Resources;

namespace Chronos.App.Tests;

/// <summary>A missing translation is no run-time failure (the fallback serves English), so this suite has to find it.</summary>
[Collection(CultureBound.Name)]
public sealed class LocalizationTests
{
    /// <summary>Keys whose Russian text is deliberately the English one. A language names itself in its own language, so the language names stay as they are.</summary>
    private static readonly string[] SameInEveryLanguage = ["AppTitle", "LanguageEnglish", "LanguageRussian"];

    [Fact]
    public void EveryKeyInTheNeutralResourceHasARussianTranslation()
    {
        var neutral = Read(CultureInfo.InvariantCulture);
        var russian = Read(CultureInfo.GetCultureInfo("ru"));

        var untranslated = neutral.Keys.Except(russian.Keys).Order().ToArray();
        var orphaned = russian.Keys.Except(neutral.Keys).Order().ToArray();

        Assert.Empty(untranslated);

        // A key only in the Russian file means its English text was renamed or deleted.
        Assert.Empty(orphaned);
    }

    [Fact]
    public void NoResourceHoldsAnEmptyString()
    {
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("ru") })
        {
            foreach (var (key, value) in Read(culture))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(value),
                    $"{key} is blank in the {Name(culture)} resource.");
            }
        }
    }

    [Fact]
    public void NoRussianTextIsStillTheEnglishOne()
    {
        var neutral = Read(CultureInfo.InvariantCulture);
        var russian = Read(CultureInfo.GetCultureInfo("ru"));

        foreach (var (key, english) in neutral)
        {
            // A key with no Russian text is the parity test's to report.
            if (SameInEveryLanguage.Contains(key) || !russian.TryGetValue(key, out var translated))
            {
                continue;
            }

            Assert.False(
                string.Equals(english, translated, StringComparison.Ordinal),
                $"{key} is word for word the English text in the Russian resource.");
        }
    }

    [Fact]
    public void ARegionalVariantOfRussianIsShownInRussian()
    {
        using var language = new UiCulture("ru-KZ");

        Assert.Equal(Read(CultureInfo.GetCultureInfo("ru"))["ServiceUnavailableHeading"], Strings.ServiceUnavailableHeading);
    }

    [Fact]
    public void ALanguageWithNoTranslationFallsBackToEnglishRatherThanToNothing()
    {
        using var language = new UiCulture("de-DE");

        var neutral = Read(CultureInfo.InvariantCulture);

        Assert.Equal(neutral["ServiceUnavailableHeading"], Strings.ServiceUnavailableHeading);
        Assert.NotEqual(Read(CultureInfo.GetCultureInfo("ru"))["ServiceUnavailableHeading"], Strings.ServiceUnavailableHeading);
    }

    /// <summary>
    /// No text in the markup. The compiler cannot see a literal in a .axaml file, so the files are read.
    /// Covers attributes and element content such as <c>&lt;TextBlock&gt;text&lt;/TextBlock&gt;</c>.
    /// </summary>
    [Fact]
    public void NoMarkupSpellsOutAWordForThePersonToRead()
    {
        foreach (var file in Repo.Markup())
        {
            // Strip comments first: a stray > in one would look like the end of a tag.
            var markup = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

            foreach (Match match in Regex.Matches(
                markup,
                // PlaceholderText as well as Watermark (12.1.1 renamed the property). ToolTipText is the tray icon's;
                // AutomationProperties are read aloud, so they must be localised too.
                """
                (?<attribute>Text|Content|Header|Title|Watermark|PlaceholderText|ToolTipText|ToolTip\.Tip|AutomationProperties\.Name|AutomationProperties\.HelpText)\s*=\s*"(?<value>[^"]*)"
                """))
            {
                var value = match.Groups["value"].Value;

                Assert.True(
                    value.StartsWith('{'),
                    $"""{Path.GetFileName(file)} spells out {match.Groups["attribute"].Value}="{value}".""");
            }

            // Anything between a closing and the next opening angle bracket. Layout leaves only whitespace there,
            // so a run of words is a typed sentence.
            foreach (Match match in Regex.Matches(markup, @">(?<content>[^<>]+)<"))
            {
                var content = match.Groups["content"].Value.Trim();

                Assert.True(
                    content.Length == 0 || content.StartsWith('{') || IsResourceValue(content),
                    $"{Path.GetFileName(file)} spells out \"{content}\" between its tags.");
            }
        }
    }

    // A colour or resource address in the theme files is a value, not a word to read.
    private static bool IsResourceValue(string content) =>
        Regex.IsMatch(content, @"^(#[0-9A-Fa-f]{6,8}|avares://\S+)$");

    /// <summary>The property answers in the new language and something announces the change.</summary>
    [Fact]
    public void ChangingTheLanguageChangesTheTextAndSaysThatItChanged()
    {
        using var restore = new UiCulture("en-US");
        var language = new LanguageSwitch();
        var text = new Text(language);
        var announced = Watch(text);

        var english = text.ServiceUnavailableHeading;
        language.Use("ru-RU");

        Assert.NotEqual(english, text.ServiceUnavailableHeading);
        Assert.Contains(nameof(Text.ServiceUnavailableHeading), announced);
    }

    /// <summary>The notification covers every property, found by reflection so a new label cannot be missed.</summary>
    [Fact]
    public void ChangingTheLanguageAnnouncesEveryPropertyThereIs()
    {
        using var restore = new UiCulture("en-US");
        var language = new LanguageSwitch();
        var text = new Text(language);
        var announced = Watch(text);

        language.Use("ru-RU");

        var properties = typeof(Text).GetProperties().Select(property => property.Name).ToArray();
        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            Assert.Contains(property, announced);
        }
    }

    [Fact]
    public void ChangingTheLanguageAndChangingItBackReturnsTheSameWords()
    {
        using var restore = new UiCulture("en-US");
        var language = new LanguageSwitch();
        var text = new Text(language);

        var english = text.ServiceUnavailableHeading;
        language.Use("ru-RU");
        var russian = text.ServiceUnavailableHeading;
        language.Use("en-US");

        Assert.NotEqual(english, russian);
        Assert.Equal(english, text.ServiceUnavailableHeading);
    }

    /// <summary>The language is process-wide: a thread started later (tray timer, command reply) uses it too.</summary>
    [Fact]
    public void AThreadStartedAfterwardsSpeaksTheLanguageThatWasChosen()
    {
        using var restore = new UiCulture("en-US");
        var language = new LanguageSwitch();
        var text = new Text(language);

        language.Use("ru-RU");

        string? elsewhere = null;

        // With the execution context suppressed, the new thread inherits nothing from this one.
        using (ExecutionContext.SuppressFlow())
        {
            var thread = new Thread(() => elsewhere = Strings.ServiceUnavailableHeading);
            thread.Start();
            thread.Join();
        }

        Assert.Equal(text.ServiceUnavailableHeading, elsewhere);
    }

    /// <summary>Choosing Russian leaves the formatting culture, which a log line would use, as the machine had it.</summary>
    [Fact]
    public void ChangingTheInterfaceLanguageLeavesTheFormattingCultureAlone()
    {
        // en-GB is neither language nor this machine's, so it survives only if nothing touched it.
        using var restore = new UiCulture("en-US", formatting: "en-GB");
        var language = new LanguageSwitch();

        language.Use("ru-RU");

        Assert.Equal("en-GB", CultureInfo.CurrentCulture.Name);
        Assert.Equal("ru-RU", CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public void TheLanguageInUseIsTheOneTheSwitchReports()
    {
        using var restore = new UiCulture("en-US");
        var language = new LanguageSwitch();

        language.Use("ru-RU");

        Assert.Equal("ru-RU", language.Current.Name);
    }

    private static List<string> Watch(Text text)
    {
        var names = new List<string>();
        ((INotifyPropertyChanged)text).PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);

        return names;
    }


    private static string Name(CultureInfo culture) =>
        culture.Equals(CultureInfo.InvariantCulture) ? "neutral" : culture.Name;

    /// <summary>The keys of one resource file; <c>tryParents: false</c> stops the Russian set reporting English fallbacks.</summary>
    private static Dictionary<string, string> Read(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.True(set is not null, $"There is no resource file for {Name(culture)}.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in set!)
        {
            values[(string)entry.Key] = entry.Value as string ?? string.Empty;
        }

        Assert.NotEmpty(values);

        return values;
    }
}
