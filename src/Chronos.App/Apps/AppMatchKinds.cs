using Chronos.App.Resources;
using Chronos.App.Services;

namespace Chronos.App.Apps;

/// <summary>One way of matching, named and explained. Built on each redraw so a language change reaches it.</summary>
public sealed record MatchKindLine(string Kind, string Name, string Explanation);

/// <summary>
/// The two ways a rule can name a program, with a sentence each. By name survives a reinstall but
/// catches namesakes; by path blocks one file and stops matching when it moves.
/// </summary>
public static class AppMatchKinds
{
    /// <summary>In display order. By name first: it survives updates and its failure (a namesake) is visible.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [AppMatch.FileName, AppMatch.FullPath];

    public static IReadOnlyList<MatchKindLine> Lines() =>
        [.. Kinds.Select(kind => new MatchKindLine(kind, Name(kind), Explanation(kind)))];

    /// <summary>The kind in a few words, read at the moment of asking so a language change reaches it.</summary>
    public static string Name(string kind) => kind switch
    {
        AppMatch.FileName => Strings.MatchFileName,
        AppMatch.FullPath => Strings.MatchFullPath,
        _ => kind,
    };

    /// <summary>What choosing it will mean later on, which is the part nobody can guess.</summary>
    public static string Explanation(string kind) => kind switch
    {
        AppMatch.FileName => Strings.MatchFileNameExplained,
        AppMatch.FullPath => Strings.MatchFullPathExplained,
        _ => string.Empty,
    };
}
