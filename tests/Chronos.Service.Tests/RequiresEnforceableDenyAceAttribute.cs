using System.Security.AccessControl;
using System.Security.Principal;

namespace Chronos.Service.Tests;

/// <summary>
/// A fact that induces a read failure by denying its own account read access with a Deny ACE. It skips where that does not work.
/// Settled by doing the thing and looking, not by predicting: an elevated token still honours an explicit Deny ACE naming its own user
/// (SeBackupPrivilege is off by default and needs FILE_FLAG_BACKUP_SEMANTICS), but under SYSTEM or on a volume without ACLs the deny does not bite.
/// The probe uses a scratch file in the temp directory the guarded test writes to. Anything unexpected counts as "cannot host this test",
/// since a throwing attribute constructor breaks discovery for the whole class.
/// </summary>
public sealed class RequiresEnforceableDenyAceAttribute : FactAttribute
{
    public RequiresEnforceableDenyAceAttribute()
    {
        Skip = WhyTheDenyWouldNotBite();
    }

    private static string? WhyTheDenyWouldNotBite()
    {
        var probe = Path.Combine(Path.GetTempPath(), $"chronos-deny-probe-{Guid.NewGuid():N}");
        FileSystemAccessRule? denial = null;
        FileInfo? file = null;

        try
        {
            File.WriteAllText(probe, "probe");

            using var identity = WindowsIdentity.GetCurrent();
            file = new FileInfo(probe);
            denial = new FileSystemAccessRule(
                identity.User!, FileSystemRights.ReadData, AccessControlType.Deny);

            var security = file.GetAccessControl();
            security.AddAccessRule(denial);
            file.SetAccessControl(security);

            try
            {
                File.ReadAllText(probe);
            }
            catch (UnauthorizedAccessException)
            {
                // The premise holds: this account cannot read what it just denied itself.
                return null;
            }

            return "This account can still read a file it has just denied itself read access to, "
                + "so the test cannot induce the failure it exists to check.";
        }
        catch (Exception exception)
        {
            return $"A Deny ACE could not be applied here, so the test cannot induce the failure "
                + $"it exists to check: {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            Clean(file, denial, probe);
        }
    }

    // Two independent attempts: if restoring the ACE throws, the file must still be deleted.
    private static void Clean(FileInfo? file, FileSystemAccessRule? denial, string probe)
    {
        try
        {
            if (file is not null && denial is not null)
            {
                var restored = file.GetAccessControl();
                restored.RemoveAccessRule(denial);
                file.SetAccessControl(restored);
            }
        }
        catch (Exception)
        {
            // Not worth taking class discovery down. Deleting the file takes the ACE with it; the restore matters only if the delete fails.
        }

        try
        {
            File.Delete(probe);
        }
        catch (Exception)
        {
            // A scratch file in the temp directory; File.Delete is a no-op when the probe was never created.
        }
    }
}
