namespace Chronos.Service.Setup;

/// <summary>The Windows task scheduler, as much of it as the recovery task needs.</summary>
public interface ITaskRegistration
{
    /// <summary>
    /// Whether the recovery task is registered. False only when the scheduler says it is absent;
    /// any other failure throws, so uninstall never reports a still-running task as removed.
    /// </summary>
    bool Exists();

    /// <summary>Registers the task from its XML, replacing one of the same name. Administrator only.</summary>
    void Register(string xml);

    /// <summary>Removes the task and its folder. Removing what is absent is not a failure.</summary>
    void Remove();
}
