using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Chronos.App.Resources;

namespace Chronos.App.Localization;

/// <summary>One language on the picker: what it is stored as, and what it calls itself.</summary>
public sealed class LanguageLine(string choice, string name) : ObservableObject
{
    private string _name = name;

    public string Choice { get; } = choice;

    /// <summary>Rewritten in place on a language change: a list handed new items while its control writes a selection puts the old selection back.</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
}

/// <summary>
/// The two languages and the codes they are stored under. The codes are neutral ("en", "ru"), not
/// regional: ru-KZ is served from the Russian resources.
/// </summary>
public static class LanguageChoice
{
    public const string English = "en";

    public const string Russian = "ru";

    /// <summary>In the order they are offered.</summary>
    public static IReadOnlyList<string> All { get; } = [English, Russian];

    /// <summary>The choices in words. Made once for a screen; a language change rewrites the names in place.</summary>
    public static IReadOnlyList<LanguageLine> Lines() =>
        [.. All.Select(choice => new LanguageLine(choice, Name(choice)))];

    /// <summary>Each language names itself, the same in both resources.</summary>
    public static string Name(string choice) => choice switch
    {
        English => Strings.LanguageEnglish,
        Russian => Strings.LanguageRussian,
        _ => choice,
    };

    /// <summary>"en" or "ru" for a stored code, a regional variant included; null for anything else.</summary>
    public static string? Known(string? stored)
    {
        var code = stored?.Trim();

        return All.FirstOrDefault(choice =>
            string.Equals(code, choice, StringComparison.OrdinalIgnoreCase)
            || (code?.StartsWith(choice + "-", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>Russian on a Russian machine, English on any other.</summary>
    public static string ForMachine(CultureInfo machine) =>
        string.Equals(machine.TwoLetterISOLanguageName, Russian, StringComparison.OrdinalIgnoreCase) ? Russian : English;

    /// <summary>Where a code sits on the list; anything unknown is the first.</summary>
    public static int IndexOf(string? stored) => Known(stored) is { } known ? All.ToList().IndexOf(known) : 0;
}
