using System.Collections.Immutable;

namespace Chronos.Core.Rules;

public sealed class BlockList
{
    private readonly ImmutableHashSet<SiteRule> _sites;
    private readonly ImmutableHashSet<AppRule> _apps;

    public BlockList(IEnumerable<SiteRule> sites, IEnumerable<AppRule> apps)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(apps);

        _sites = sites.ToImmutableHashSet();
        _apps = apps.ToImmutableHashSet();
    }

    public static BlockList Empty { get; } = new([], []);

    public IReadOnlyCollection<SiteRule> Sites => _sites;

    public IReadOnlyCollection<AppRule> Apps => _apps;

    public bool IsEmpty => _sites.Count == 0 && _apps.Count == 0;

    public BlockList WithSite(SiteRule rule) => new([.. _sites, rule], _apps);

    public BlockList WithApp(AppRule rule) => new(_sites, [.. _apps, rule]);

    public BlockList WithoutSite(SiteRule rule) => new(_sites.Where(site => site != rule), _apps);

    public BlockList WithoutApp(AppRule rule) => new(_sites, _apps.Where(app => app != rule));
}
