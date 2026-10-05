namespace Chronos.Core.Sessions;

/// <summary>What the engine made of a command. The reason is a code from <see cref="SessionCodes"/>.</summary>
public sealed record CommandResult(bool Accepted, string? RejectionReason, IReadOnlyList<SessionNotice> Warnings)
{
    private static readonly IReadOnlyList<SessionNotice> NoWarnings = [];

    public static CommandResult Ok() => new(true, null, NoWarnings);

    public static CommandResult Ok(IReadOnlyList<SessionNotice> warnings) => new(true, null, warnings);

    public static CommandResult Reject(string reason) => new(false, reason, NoWarnings);

    public static CommandResult Reject(string reason, IReadOnlyList<SessionNotice> warnings) =>
        new(false, reason, warnings);
}
