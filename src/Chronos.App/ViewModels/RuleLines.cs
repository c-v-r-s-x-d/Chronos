using Chronos.Ipc;

namespace Chronos.App.ViewModels;

/// <summary>One session length on offer: a chip, or the one that means "type your own".</summary>
public sealed record DurationLine(int Minutes, string Name, bool IsCustom);

/// <summary>One site rule as a line: the rule, the letter in its square, and its remove button in words.</summary>
public sealed record SiteLine(SiteRuleMessage Rule, string Letter, string RemoveName);

/// <summary>One app rule as a line: the rule, its letter, how it matches and its remove button, in words.</summary>
public sealed record AppLine(AppRuleMessage Rule, string Letter, string Kind, string RemoveName);
