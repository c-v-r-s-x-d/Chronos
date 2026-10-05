namespace Chronos.Core.Enforcement;

public interface IEnforcer
{
    string Name { get; }

    /// <summary>Checks whether this layer can currently be reconciled.</summary>
    Task<EnforcerAvailability> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// Brings this layer in line with the plan. Must be idempotent: running twice with the
    /// same plan must not touch the system a second time.
    /// </summary>
    Task<ReconcileResult> ReconcileAsync(EnforcementPlan plan, CancellationToken ct);

    /// <summary>Removes every change made by this layer, regardless of the current plan.</summary>
    Task ClearAsync(CancellationToken ct);
}
