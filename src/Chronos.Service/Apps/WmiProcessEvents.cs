using System.Management;

namespace Chronos.Service.Apps;

/// <summary>
/// The fallback source. It reports only the executable name, so full-path rules cannot match.
/// </summary>
public sealed class WmiProcessEvents : IProcessEvents
{
    private ManagementEventWatcher? _watcher;
    private Action<ProcessStarted>? _onStarted;

    public string SourceName => "wmi";

    // No pump thread to lose: a watcher failure surfaces as an exception at start.
    public bool IsFaulted => false;

    public void Start(Action<ProcessStarted> onStarted)
    {
        ArgumentNullException.ThrowIfNull(onStarted);

        // A second watcher would deliver every event twice.
        if (_watcher is not null)
        {
            return;
        }

        _onStarted = onStarted;

        var watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        watcher.EventArrived += OnEventArrived;

        // Assigned before Start can throw, so Dispose can always reach it.
        _watcher = watcher;

        try
        {
            watcher.Start();
        }
        catch
        {
            _watcher = null;
            _onStarted = null;
            watcher.EventArrived -= OnEventArrived;
            watcher.Dispose();

            throw;
        }
    }

    public void Dispose()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EventArrived -= OnEventArrived;
        _watcher.Stop();
        _watcher.Dispose();
        _watcher = null;
        _onStarted = null;
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs args)
    {
        var name = args.NewEvent["ProcessName"] as string;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var processId = Convert.ToInt32(args.NewEvent["ProcessID"], System.Globalization.CultureInfo.InvariantCulture);
        _onStarted?.Invoke(new ProcessStarted(processId, name));
    }
}
