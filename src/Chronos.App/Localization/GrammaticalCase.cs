namespace Chronos.App.Localization;

/// <summary>
/// What a length of time is in when put inside a sentence. Russian «через» takes the accusative;
/// English declines nothing. Two cases, one per sentence shape.
/// </summary>
public enum GrammaticalCase
{
    /// <summary>The naming form, and what a duration standing on its own is in.</summary>
    Nominative,

    /// <summary>What follows «через». The reason this enumeration exists.</summary>
    Accusative,
}
