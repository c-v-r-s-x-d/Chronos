namespace Chronos.App.ViewModels;

/// <summary>
/// The sidebar's sections, in the order they are listed. Navigation within a screen,
/// not a state: a state change leaves the section where it was.
/// </summary>
public enum Section
{
    Session,
    Sites,
    Apps,
    Settings,
}

/// <summary>One sidebar entry, in words, with the lock shown on the lists during a session.</summary>
public sealed record SectionLine(Section Section, string Name, bool Locked);
