using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>
/// The service manager, remembered instead of called: registering a service needs administrator
/// rights and leaves one on the machine. It keeps one state and answers <see cref="Query"/> from it.
/// Each call that changes something writes its name into the shared list first and fails afterwards,
/// so an order test sees the step that was attempted.
/// </summary>
internal sealed class FakeServiceRegistration : IServiceRegistration
{
    private readonly List<string> _steps;

    public FakeServiceRegistration()
        : this([])
    {
    }

    public FakeServiceRegistration(List<string> steps) => _steps = steps;

    public ServiceRunState State { get; set; } = ServiceRunState.NotInstalled;

    /// <summary>What was registered, or null on a machine where nothing was.</summary>
    public ServiceDefinition? Installed { get; private set; }

    public TimeSpan? StartTimeout { get; private set; }

    public TimeSpan? StopTimeout { get; private set; }

    public Exception? QueryThrows { get; set; }

    public Exception? InstallThrows { get; set; }

    public Exception? UpdateThrows { get; set; }

    public Exception? RemoveThrows { get; set; }

    public Exception? StartThrows { get; set; }

    public Exception? StopThrows { get; set; }

    public ServiceRunState Query() => QueryThrows is { } failure ? throw failure : State;

    public void Install(ServiceDefinition definition)
    {
        _steps.Add("install-service");

        if (InstallThrows is { } failure)
        {
            throw failure;
        }

        Installed = definition;
        State = ServiceRunState.Stopped;
    }

    /// <summary>Records the definition as <see cref="Install"/> does and leaves the run state alone.</summary>
    public void Update(ServiceDefinition definition)
    {
        _steps.Add("update-service");

        if (UpdateThrows is { } failure)
        {
            throw failure;
        }

        Installed = definition;
    }

    public void Remove()
    {
        _steps.Add("remove-service");

        if (RemoveThrows is { } failure)
        {
            throw failure;
        }

        Installed = null;
        State = ServiceRunState.NotInstalled;
    }

    public void Start(TimeSpan timeout)
    {
        _steps.Add("start-service");
        StartTimeout = timeout;

        if (StartThrows is { } failure)
        {
            throw failure;
        }

        State = ServiceRunState.Running;
    }

    public void Stop(TimeSpan timeout)
    {
        _steps.Add("stop");
        StopTimeout = timeout;

        if (StopThrows is { } failure)
        {
            throw failure;
        }

        State = ServiceRunState.Stopped;
    }
}
