using Chronos.Core.Enforcement;
using Chronos.Core.Sessions;
using Chronos.Ipc;
using Chronos.Service.Ipc;
using Chronos.Service.Sessions;
using Chronos.Service.State;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Reconciliation;

public sealed class ReconcileRunner(
    SessionEngine engine,
    ReconcileCoordinator coordinator,
    LayerStatusRegistry layers,
    StateStore state,
    SessionGate gate,
    EventBus events,
    // A delegate, not the dispatcher: the dispatcher reaches this class through the scheduler.
    Func<StatusPayload> status,
    ILogger<ReconcileRunner> logger)
{
    private readonly Lock _unavailableSync = new();

    // The reason each layer was last reported unavailable for; absent while it is available.
    private readonly Dictionary<string, string?> _unavailable = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ReconcileResult>> RunOnceAsync(CancellationToken ct)
    {
        var cycleId = Guid.NewGuid().ToString("N")[..8];

        EnforcementPlan plan;
        bool ended;
        Guid? engineSessionId;
        SessionState before;

        lock (gate.Sync)
        {
            // Compared after the pass, so an event is published only when the state moved.
            before = engine.State;

            engine.Tick();

            if (engine.Session is { } session && engine.State is not SessionState.Ended)
            {
                state.Save(session);
            }

            plan = engine.CurrentPlan();
            ended = engine.State is SessionState.Ended;
            engineSessionId = engine.Session?.Id;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["SessionId"] = engineSessionId,
        });

        var results = await coordinator.RunAsync(plan, ct).ConfigureAwait(false);

        // Before the log, so a failing log sink cannot lose the layer status.
        layers.Record(results);
        Report(cycleId, results, probed: true);

        if (ended)
        {
            // Not recorded: clearing does not probe availability.
            var cleared = await coordinator.ClearAllAsync(ct).ConfigureAwait(false);
            Report(cycleId, cleared);

            lock (gate.Sync)
            {
                engine.ConfirmCleared();
                state.Clear();
            }

            logger.LogInformation("Cycle {CycleId}: session ended, every layer cleared.", cycleId);
        }

        // After the clearing, so the event reflects the final state.
        PublishIfTheStateMoved(before);

        return results;
    }

    private void PublishIfTheStateMoved(SessionState before)
    {
        lock (gate.Sync)
        {
            if (engine.State == before)
            {
                return;
            }
        }

        events.Publish(IpcEvent.StatusChanged(status()));
    }

    /// <param name="probed">False for a clear, which neither reports a layer unavailable nor available again.</param>
    private void Report(string cycleId, IReadOnlyList<ReconcileResult> results, bool probed = false)
    {
        foreach (var result in results)
        {
            switch (result.Outcome)
            {
                case ReconcileOutcome.Failed:
                    logger.LogError(
                        result.Error,
                        "Cycle {CycleId}: layer {Layer} failed: {Detail}",
                        cycleId, result.EnforcerName, result.Detail);
                    break;

                case ReconcileOutcome.Skipped:
                    // Once per reason: the pass repeats every fifteen seconds.
                    logger.Log(
                        WhetherNews(result.EnforcerName, result.Detail) ? LogLevel.Information : LogLevel.Debug,
                        "Cycle {CycleId}: layer {Layer} is unavailable: {Detail}",
                        cycleId, result.EnforcerName, result.Detail);
                    break;

                case ReconcileOutcome.Changed:
                    NoteAvailable(cycleId, result, probed);
                    logger.LogInformation(
                        "Cycle {CycleId}: layer {Layer} applied {Applied} and removed {Removed}.",
                        cycleId, result.EnforcerName, result.Applied, result.Removed);
                    break;

                case ReconcileOutcome.Unchanged:
                    NoteAvailable(cycleId, result, probed);
                    break;

                default:
                    break;
            }
        }
    }

    private bool WhetherNews(string layer, string? reason)
    {
        lock (_unavailableSync)
        {
            var news = !_unavailable.TryGetValue(layer, out var last)
                       || !string.Equals(last, reason, StringComparison.Ordinal);
            _unavailable[layer] = reason;

            return news;
        }
    }

    private void NoteAvailable(string cycleId, ReconcileResult result, bool probed)
    {
        if (!probed)
        {
            return;
        }

        bool wasUnavailable;
        lock (_unavailableSync)
        {
            wasUnavailable = _unavailable.Remove(result.EnforcerName);
        }

        if (wasUnavailable)
        {
            logger.LogInformation("Cycle {CycleId}: layer {Layer} is available again.", cycleId, result.EnforcerName);
        }
    }
}
