namespace Chronos.Service.Configuration;

public sealed record SiteRuleDto(string Domain, bool IncludeSubdomains);

public sealed record AppRuleDto(string MatchKind, string Value);
