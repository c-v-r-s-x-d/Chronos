namespace Chronos.Service.Setup;

/// <summary>The entry that starts the interface at logon, as much of it as uninstall needs.</summary>
public interface IAutostartEntry
{
    /// <summary>Whether the entry is there. Says false when it cannot tell.</summary>
    bool IsEnabled();

    /// <summary>Takes the entry away. Nothing there is not a failure.</summary>
    void Disable();
}
