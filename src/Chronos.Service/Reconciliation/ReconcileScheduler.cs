using Microsoft.Extensions.Logging;

namespace Chronos.Service.Reconciliation;

public sealed class ReconcileScheduler(ReconcileRunner runner, ILogger<ReconcileScheduler> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wakeUp = new(0, 1);

    private string _pendingReason = "timer";

    public static TimeSpan Period { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Runs one pass, waiting for any pass already in progress to finish first.</summary>
    public async Task RunPendingAsync(string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!string.Equals(reason, "timer", StringComparison.Ordinal))
        {
            logger.LogInformation("Reconcile pass running: {Reason}.", reason);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await runner.RunOnceAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks for a pass now instead of at the next tick.</summary>
    public void RequestImmediate(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        _pendingReason = reason;
        logger.LogInformation("Reconcile requested: {Reason}.", reason);

        // Capacity is one, so a burst collapses into one pass. Checking CurrentCount first would
        // race; a full semaphore means somebody already asked.
        try
        {
            _wakeUp.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>Completes when the period elapses or somebody asks for an immediate pass.</summary>
    public async Task<string> WaitForNextTriggerAsync(CancellationToken ct)
    {
        if (await _wakeUp.WaitAsync(Period, ct).ConfigureAwait(false))
        {
            return _pendingReason;
        }

        return "timer";
    }
}
