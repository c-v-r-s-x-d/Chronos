using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Chronos.Service.Setup;

/// <summary>
/// The directory for configuration, state and logs. SYSTEM and Administrators may write, Users may
/// only read: a user who could write here could edit the block list or state of their own session.
/// Inheritance is off because %ProgramData% grants rights this directory must not have.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirectory
{
    /// <summary>Creates the directory if needed and sets its rights either way; safe to repeat.</summary>
    public static void Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Directory.CreateDirectory(path);

        // A fresh list, so nothing a wider earlier setup or a manual edit left behind survives.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        Allow(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Allow(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);

        // Read and execute: the interface reads the logs, and that needs traversal.
        Allow(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);

        directory.SetAccessControl(security);
    }

    private static void Allow(DirectorySecurity security, WellKnownSidType sid, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sid, domainSid: null),
            rights,

            // Both flags so files created inside get the same rules; None applies them to the directory too.
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
}
