namespace Chronos.App.ViewModels;

/// <summary>
/// The "Sites" section over whichever screen is up. A type of its own so the window picks the page
/// by template; the lists and commands stay on the screen, which the shell keeps across statuses.
/// </summary>
public sealed class SitesSection(RuleScreenViewModel rules)
{
    public RuleScreenViewModel Rules { get; } = rules ?? throw new ArgumentNullException(nameof(rules));
}

/// <summary>The "Apps" section, the same way.</summary>
public sealed class AppsSection(RuleScreenViewModel rules)
{
    public RuleScreenViewModel Rules { get; } = rules ?? throw new ArgumentNullException(nameof(rules));
}
