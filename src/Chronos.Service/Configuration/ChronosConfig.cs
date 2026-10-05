using Chronos.Core.Rules;
using Chronos.Core.Sessions;

namespace Chronos.Service.Configuration;

public sealed record ChronosConfig
{
    public IReadOnlyList<SiteRuleDto> Sites { get; init; } = [];

    public IReadOnlyList<AppRuleDto> Apps { get; init; } = [];

    public int CoolDownMinutes { get; init; } = (int)SessionLimits.DefaultCoolDown.TotalMinutes;

    public int DefaultSessionMinutes { get; init; } = (int)SessionLimits.DefaultSessionDuration.TotalMinutes;

    public string Language { get; init; } = "system";

    public bool VerboseLogging { get; init; }

    /// <summary>Switch for the address (WFP) layer. On by default.</summary>
    public bool WfpEnabled { get; init; } = true;

    public BlockList ToBlockList()
    {
        var sites = new List<SiteRule>();
        foreach (var site in Sites)
        {
            if (!string.IsNullOrWhiteSpace(site.Domain))
            {
                sites.Add(new SiteRule(site.Domain, site.IncludeSubdomains));
            }
        }

        var apps = new List<AppRule>();
        foreach (var app in Apps)
        {
            // A hand-edited file can carry a bad kind; drop the line. IsDefined is needed because
            // Enum.TryParse accepts any number.
            if (Enum.TryParse<AppMatchKind>(app.MatchKind, ignoreCase: true, out var kind)
                && Enum.IsDefined(kind)
                && !string.IsNullOrWhiteSpace(app.Value))
            {
                apps.Add(new AppRule(kind, app.Value));
            }
        }

        return new BlockList(sites, apps);
    }

    public static ChronosConfig FromBlockList(BlockList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        return new ChronosConfig
        {
            Sites = [.. list.Sites.Select(site => new SiteRuleDto(site.Domain, site.IncludeSubdomains))],
            Apps = [.. list.Apps.Select(app => new AppRuleDto(app.Kind.ToString(), app.Value))],
        };
    }
}
