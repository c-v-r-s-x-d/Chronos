using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Chronos.Service.Tests;

/// <summary>
/// Removes a directory a test created, including one whose rights no longer let this process remove it.
/// <see cref="Setup.DataDirectory"/> leaves an unelevated account read and execute only; the owner may always rewrite the list, which this does before deleting.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TestDirectory
{
    public static void Delete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();

        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        directory.SetAccessControl(security);

        Directory.Delete(path, recursive: true);
    }
}
