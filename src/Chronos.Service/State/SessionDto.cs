using Chronos.Core.Rules;
using Chronos.Core.Sessions;
using Chronos.Service.Configuration;

namespace Chronos.Service.State;

public sealed record SessionDto
{
    public Guid Id { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset EndsAt { get; init; }

    public int CoolDownMinutes { get; init; }

    public DateTimeOffset? UnlockRequestedAt { get; init; }

    public DateTimeOffset? UnlockEffectiveAt { get; init; }

    public IReadOnlyList<SiteRuleDto> Sites { get; init; } = [];

    public IReadOnlyList<AppRuleDto> Apps { get; init; } = [];

    /// <summary>A file can parse cleanly yet hold no usable session; accepting it would strand the engine.</summary>
    public bool IsComplete() =>
        Id != Guid.Empty
        && StartedAt != default
        && EndsAt != default
        && CoolDownMinutes > 0;

    public static SessionDto FromDomain(BlockSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SessionDto
        {
            Id = session.Id,
            StartedAt = session.StartedAt,
            EndsAt = session.EndsAt,
            CoolDownMinutes = (int)session.CoolDown.TotalMinutes,
            UnlockRequestedAt = session.Unlock?.RequestedAt,
            UnlockEffectiveAt = session.Unlock?.EffectiveAt,
            Sites = [.. session.Rules.Sites.Select(site => new SiteRuleDto(site.Domain, site.IncludeSubdomains))],
            Apps = [.. session.Rules.Apps.Select(app => new AppRuleDto(app.Kind.ToString(), app.Value))],
        };
    }

    public BlockSession ToDomain()
    {
        var sites = Sites
            .Where(site => !string.IsNullOrWhiteSpace(site.Domain))
            .Select(site => new SiteRule(site.Domain, site.IncludeSubdomains));

        var apps = Apps
            .Where(app => Enum.TryParse<AppMatchKind>(app.MatchKind, ignoreCase: true, out _)
                && !string.IsNullOrWhiteSpace(app.Value))
            .Select(app => new AppRule(Enum.Parse<AppMatchKind>(app.MatchKind, ignoreCase: true), app.Value));

        var unlock = UnlockRequestedAt is { } requested && UnlockEffectiveAt is { } effective
            ? new UnlockRequest(requested, effective)
            : null;

        return new BlockSession
        {
            Id = Id,
            StartedAt = StartedAt,
            EndsAt = EndsAt,
            CoolDown = TimeSpan.FromMinutes(CoolDownMinutes),
            Rules = new BlockList(sites, apps),
            Unlock = unlock,
        };
    }
}
