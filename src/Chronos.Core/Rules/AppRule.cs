namespace Chronos.Core.Rules;

public enum AppMatchKind
{
    FullPath,
    FileName,
}

public sealed record AppRule
{
    public AppRule(AppMatchKind kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Kind = kind;
        Value = value.Trim();
    }

    public AppMatchKind Kind { get; }

    public string Value { get; }
}
