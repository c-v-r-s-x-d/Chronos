namespace Chronos.Core.Enforcement;

public sealed class ReconcileCoordinator
{
    private readonly IReadOnlyList<IEnforcer> _enforcers;

    public ReconcileCoordinator(IEnumerable<IEnforcer> enforcers)
    {
        ArgumentNullException.ThrowIfNull(enforcers);

        _enforcers = [.. enforcers];
    }

    /// <summary>The layers this coordinator drives, in order. Exposed so composition can be asserted.</summary>
    public IReadOnlyList<IEnforcer> Enforcers => _enforcers;

    /// <summary>Reason code for an unhandled exception. A code, not <see cref="Exception.Message"/>, which is localized; the exception itself is kept in <see cref="ReconcileResult.Error"/>.</summary>
    public static string UnhandledReason(string enforcerName) => $"{enforcerName}.unhandled";

    /// <summary>Reason code for a layer that is unavailable without giving a reason.</summary>
    public static string UnavailableReason(string enforcerName) => $"{enforcerName}.unavailable";

    public async Task<IReadOnlyList<ReconcileResult>> RunAsync(EnforcementPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var results = new List<ReconcileResult>(_enforcers.Count);

        foreach (var enforcer in _enforcers)
        {
            results.Add(await RunOneAsync(enforcer, plan, ct).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<IReadOnlyList<ReconcileResult>> ClearAllAsync(CancellationToken ct)
    {
        var results = new List<ReconcileResult>(_enforcers.Count);

        foreach (var enforcer in _enforcers)
        {
            try
            {
                await enforcer.ClearAsync(ct).ConfigureAwait(false);
                results.Add(ReconcileResult.Cleared(enforcer.Name));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One layer failing to clear must not stop the others.
                results.Add(ReconcileResult.Failed(enforcer.Name, UnhandledReason(enforcer.Name), exception));
            }
        }

        return results;
    }

    private static async Task<ReconcileResult> RunOneAsync(
        IEnforcer enforcer,
        EnforcementPlan plan,
        CancellationToken ct)
    {
        try
        {
            var availability = await enforcer.ProbeAsync(ct).ConfigureAwait(false);
            if (!availability.IsAvailable)
            {
                return ReconcileResult.Skipped(enforcer.Name, availability.Reason ?? UnavailableReason(enforcer.Name));
            }

            return await enforcer.ReconcileAsync(plan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ReconcileResult.Failed(enforcer.Name, UnhandledReason(enforcer.Name), exception);
        }
    }
}
