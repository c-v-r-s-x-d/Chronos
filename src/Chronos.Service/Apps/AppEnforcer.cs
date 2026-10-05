using Chronos.Core.Enforcement;
using Chronos.Core.Time;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.Apps;

public sealed class AppEnforcer(
    AppWatchdog watchdog,
    Func<Action<ProcessStarted>, IProcessEvents> createEvents,
    IClock clock,
    ILogger<AppEnforcer> logger) : IEnforcer, IDisposable
{
    public const string LayerName = "apps";

    // A stable code, not a sentence: the reason reaches a bilingual UI.
    public const string NoEventSource = "apps.no-event-source";

    /// <summary>How long the layer reports unavailable after a failed start before it tries again.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    private readonly Lock _sync = new();

    private IProcessEvents? _events;
    private AppRuleIndex _armed = AppRuleIndex.Empty;
    private DateTimeOffset? _retryNotBefore;

    public string Name => LayerName;

    public Task<EnforcerAvailability> ProbeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            // Before the first session the source has not been tried yet, so report available.
            return Task.FromResult(_retryNotBefore is { } notBefore && clock.UtcNow < notBefore
                ? EnforcerAvailability.Unavailable(NoEventSource)
                : EnforcerAvailability.Available);
        }
    }

    public Task<ReconcileResult> ReconcileAsync(EnforcementPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();

        var desired = AppRuleIndex.Build(plan.Apps);

        IProcessEvents? faulted;
        bool needsSource;

        lock (_sync)
        {
            // Checked before the short-circuit: a faulted source leaves the plan unchanged, and
            // dropping it here is the only way the layer recovers from a dead pump.
            faulted = _events?.IsFaulted == true ? TakeSource() : null;

            if (faulted is null && _armed.SameAs(desired))
            {
                return Task.FromResult(ReconcileResult.Unchanged(Name));
            }

            needsSource = !desired.IsEmpty && _events is null;
        }

        // Disposing joins the pump thread and creating a source can take tens of seconds against
        // an unhealthy WMI service. Layers run one after another, so keep both outside the lock.
        faulted?.Dispose();

        IProcessEvents? started = null;

        if (needsSource)
        {
            try
            {
                // The factory starts the source with the handler; starting it again would leak a second session.
                started = createEvents(watchdog.OnProcessStarted);
            }
            catch (Exception exception)
            {
                // A failed result, not an exception: the other layers must still be applied.
                logger.LogError(exception, "No process event source could be started.");

                lock (_sync)
                {
                    _retryNotBefore = clock.UtcNow + RetryInterval;

                    // Forget the armed rules, or the next cycle matches the plan and short-circuits
                    // to Unchanged, hiding the failure.
                    _armed = AppRuleIndex.Empty;
                }

                return Task.FromResult(ReconcileResult.Failed(Name, NoEventSource));
            }
        }

        IProcessEvents? surplus;
        string? sourceName = null;

        lock (_sync)
        {
            if (started is not null && _events is null)
            {
                _events = started;
                started = null;
                sourceName = _events.SourceName;
            }

            // An empty plan must not leave a trace session and pump thread running.
            surplus = desired.IsEmpty ? TakeSource() : null;

            _armed = desired;
            _retryNotBefore = null;
        }

        // A source another thread beat us to, or one an empty plan no longer needs.
        started?.Dispose();
        surplus?.Dispose();

        if (sourceName is not null)
        {
            logger.LogInformation("Watching process starts through the {Source} source.", sourceName);
        }

        watchdog.Arm(desired);

        // Apps already running when the session began are covered too.
        var swept = watchdog.SweepExisting();

        return Task.FromResult(ReconcileResult.Changed(Name, applied: desired.Count, removed: swept));
    }

    public Task ClearAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        watchdog.Disarm();

        IProcessEvents? events;

        lock (_sync)
        {
            _armed = AppRuleIndex.Empty;
            _retryNotBefore = null;
            events = TakeSource();
        }

        events?.Dispose();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IProcessEvents? events;

        lock (_sync)
        {
            events = TakeSource();
        }

        events?.Dispose();
    }

    /// <summary>Hands the source over to the caller, which disposes it outside the lock.</summary>
    private IProcessEvents? TakeSource()
    {
        var events = _events;
        _events = null;

        return events;
    }
}
