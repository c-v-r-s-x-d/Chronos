using Chronos.Service.Apps;

namespace Chronos.Service.Tests;

internal sealed class FakeProcessControl : IProcessControl
{
    public List<RunningProcess> Running { get; } = [];

    public List<int> Killed { get; } = [];

    /// <summary>Process ids that report themselves as already gone, as a real one does.</summary>
    public HashSet<int> AlreadyGone { get; } = [];

    /// <summary>Process ids that are alive but refuse to be terminated.</summary>
    public HashSet<int> Denied { get; } = [];

    /// <summary>What the operating system would answer for a process id, as the fallback source
    /// reports a bare executable name and the protection check needs a path.</summary>
    public Dictionary<int, string> Paths { get; } = [];

    public int ListCalls { get; private set; }

    /// <summary>How many times a process's path was resolved through the operating system —
    /// the per-event cost that must stay zero unless a full-path rule is armed.</summary>
    public int ImagePathOfCalls { get; private set; }

    public string? ImagePathOf(int processId)
    {
        ImagePathOfCalls++;
        return Paths.TryGetValue(processId, out var path) ? path : null;
    }

    public IReadOnlyList<RunningProcess> List()
    {
        ListCalls++;
        return [.. Running];
    }

    public KillOutcome Kill(int processId)
    {
        Killed.Add(processId);

        if (AlreadyGone.Contains(processId))
        {
            return KillOutcome.AlreadyGone;
        }

        return Denied.Contains(processId) ? KillOutcome.Denied : KillOutcome.Terminated;
    }
}
