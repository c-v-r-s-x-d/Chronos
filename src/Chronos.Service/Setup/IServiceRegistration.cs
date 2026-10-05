namespace Chronos.Service.Setup;

/// <summary>What the service manager says the service is doing, or NotInstalled.</summary>
public enum ServiceRunState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>The Windows service manager, as much of it as installation and recovery need.</summary>
public interface IServiceRegistration
{
    ServiceRunState Query();

    void Install(ServiceDefinition definition);

    /// <summary>
    /// Brings an already registered service in line with the definition without removing it, which
    /// would drop the block on a machine holding a session. Everything in the definition is sent,
    /// including the failure actions and the flag that enables them.
    /// </summary>
    void Update(ServiceDefinition definition);

    void Remove();

    /// <summary>Starts the service and waits for it to be running, or throws.</summary>
    void Start(TimeSpan timeout);

    /// <summary>Stops the service and waits for it to be stopped, or throws.</summary>
    void Stop(TimeSpan timeout);
}
