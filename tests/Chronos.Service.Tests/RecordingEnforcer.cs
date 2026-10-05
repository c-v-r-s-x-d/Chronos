using Chronos.Core.Enforcement;

namespace Chronos.Service.Tests;

internal sealed class RecordingEnforcer(string name) : IEnforcer
{
    public string Name { get; } = name;

    public EnforcerAvailability Availability { get; set; } = EnforcerAvailability.Available;

    /// <summary>Answer Unchanged, as an idle pass does, instead of Changed.</summary>
    public bool ReportsNoChange { get; set; }

    public List<EnforcementPlan> ReconciledPlans { get; } = [];

    public int ClearCalls { get; private set; }

    public Task<EnforcerAvailability> ProbeAsync(CancellationToken ct) => Task.FromResult(Availability);

    public Task<ReconcileResult> ReconcileAsync(EnforcementPlan plan, CancellationToken ct)
    {
        ReconciledPlans.Add(plan);
        return Task.FromResult(
            ReportsNoChange
                ? ReconcileResult.Unchanged(Name)
                : ReconcileResult.Changed(Name, applied: plan.Sites.Count, removed: 0));
    }

    public Task ClearAsync(CancellationToken ct)
    {
        ClearCalls++;
        return Task.CompletedTask;
    }
}
