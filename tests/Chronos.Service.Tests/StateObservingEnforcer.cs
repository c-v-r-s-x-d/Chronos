using Chronos.Core.Enforcement;

namespace Chronos.Service.Tests;

/// <summary>Records whether the state file was on disk when the plan reached the enforcers, to observe the persist-before-apply order.</summary>
internal sealed class StateObservingEnforcer(Func<bool> stateFileExists) : IEnforcer
{
    public string Name => "state-observer";

    public bool StateFileExistedDuringReconcile { get; private set; }

    public Task<EnforcerAvailability> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(EnforcerAvailability.Available);

    public Task<ReconcileResult> ReconcileAsync(EnforcementPlan plan, CancellationToken ct)
    {
        StateFileExistedDuringReconcile = stateFileExists();
        return Task.FromResult(ReconcileResult.Unchanged(Name));
    }

    public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
}
