using Chronos.Core.Enforcement;
using Chronos.Core.Time;
using Chronos.Ipc;

namespace Chronos.Service.Reconciliation;

/// <summary>
/// What the last reconcile pass learned about each layer, so a status request needs no probing.
/// <see cref="LayerStatus.ObservedAt"/> says how old the answer is.
/// </summary>
public sealed class LayerStatusRegistry(IClock clock)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, LayerStatus> _layers = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public void Record(IReadOnlyList<ReconcileResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var observedAt = clock.UtcNow;

        lock (_sync)
        {
            foreach (var result in results)
            {
                if (!_layers.ContainsKey(result.EnforcerName))
                {
                    _order.Add(result.EnforcerName);
                }

                _layers[result.EnforcerName] = Describe(result, observedAt);
            }
        }
    }

    /// <summary>The layers reported on so far, in the order they were first seen.</summary>
    public IReadOnlyList<LayerStatus> Current()
    {
        lock (_sync)
        {
            return [.. _order.Select(name => _layers[name])];
        }
    }

    private static LayerStatus Describe(ReconcileResult result, DateTimeOffset observedAt)
    {
        // Skipped (cannot run) and Failed (tried and could not) both count as unavailable.
        var available = result.Outcome is ReconcileOutcome.Unchanged or ReconcileOutcome.Changed;

        return new LayerStatus(
            result.EnforcerName,
            available,
            available ? null : result.Detail,
            result.Outcome.ToString(),
            observedAt);
    }
}
