using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Chronos.Service.Apps;

/// <summary>
/// Process-start notifications from the kernel trace session. Only the process keyword is enabled;
/// image-load events would arrive in the hundreds per start.
/// </summary>
public sealed class EtwProcessEvents(DeviceMap devices, ILogger<EtwProcessEvents> logger) : IProcessEvents
{
    /// <summary>64 KB buffers in a 2 MB pool: 32 buffers.</summary>
    internal const int BufferQuantumKB = 64;
    internal const int BufferPoolMB = 2;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private TraceEventSession? _session;
    private Thread? _pump;
    private Action<ProcessStarted>? _onStarted;

    private volatile bool _stopping;

    private volatile bool _faulted;

    public string SourceName => "etw";

    public bool IsFaulted => _faulted;

    public void Start(Action<ProcessStarted> onStarted)
    {
        ArgumentNullException.ThrowIfNull(onStarted);
        ArgumentNullException.ThrowIfNull(devices);

        if (_session is not null)
        {
            return;
        }

        // Set before the session exists so the first event is not missed.
        _onStarted = onStarted;

        // A kernel session outlives its process, and a second one under the same name cannot be
        // created. Stop a leftover one so a restart does not fall back to WMI.
        TraceEventSession.GetActiveSession(KernelTraceEventParser.KernelSessionName)?.Stop();

        var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName)
        {
            BufferQuantumKB = BufferQuantumKB,
            BufferSizeMB = BufferPoolMB,
            StopOnDispose = true,
        };

        // Assigned before anything can throw, so Dispose can always reach the kernel session.
        _session = session;

        try
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            session.Source.Kernel.ProcessStart += Deliver;

            // Source.Process() blocks until the session stops, so it gets its own thread.
            _pump = new Thread(() => Pump(session))
            {
                IsBackground = true,
                Name = "chronos-process-events",
            };
            _pump.Start();
        }
        catch
        {
            _session = null;
            _onStarted = null;
            session.Dispose();

            throw;
        }
    }

    public void Dispose()
    {
        var session = _session;
        var pump = _pump;

        _session = null;
        _pump = null;
        _onStarted = null;

        // Set first so the pump can tell a deliberate stop from a failure.
        _stopping = true;

        session?.Dispose();

        // Join so no subscriber is called after Dispose returns. The timeout protects shutdown;
        // the identity check avoids joining the pump from itself.
        if (pump is not null && pump != Thread.CurrentThread)
        {
            pump.Join(StopTimeout);
        }
    }

    private void Deliver(Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessTraceData data)
    {
        try
        {
            // DeviceMap is a backstop for volumes attached after TraceEvent built its own map.
            _onStarted?.Invoke(new ProcessStarted(data.ProcessID, devices.ToDosPath(data.ImageFileName)));
        }
        catch (Exception exception)
        {
            // Subscribers run on this thread; an escaping exception would kill the service.
            logger.LogError(exception, "A process-start subscriber failed; the event was dropped.");
        }
    }

    private void Pump(TraceEventSession session)
    {
        try
        {
            session.Source.Process();

            // A normal return still ends the pump: expected on a deliberate stop, a fault otherwise.
            Fault(exception: null);
        }
        catch (Exception exception)
        {
            // An unhandled exception here would end the process, so record it and end the thread.
            Fault(exception);
        }
    }

    private void Fault(Exception? exception)
    {
        if (_stopping)
        {
            // A clean session end must not log an Error.
            logger.LogDebug(exception, "The process event pump ended with the session.");
            return;
        }

        _faulted = true;
        logger.LogError(exception, "The process event pump stopped; application blocking is no longer active.");
    }
}
