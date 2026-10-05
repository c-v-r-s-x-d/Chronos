namespace Chronos.App.Time;

/// <summary>The machine's clock behind a seam, so no test waits on wall time. Separate from the domain's <c>IClock</c>: the interface references only the protocol.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
