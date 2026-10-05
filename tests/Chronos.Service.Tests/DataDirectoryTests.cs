using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Chronos.Service.Configuration;
using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

[SupportedOSPlatform("windows")]
public sealed class DataDirectoryTests : IDisposable
{
    // A directory of this test's own under the temporary path, so the real %ProgramData%\Chronos is never touched.
    private readonly string _path = Path.Combine(Path.GetTempPath(), "chronos-acl-" + Guid.NewGuid().ToString("N"));

    /// <summary>FILE_GENERIC_READ | FILE_GENERIC_EXECUTE and nothing else. SYNCHRONIZE is in it because
    /// <c>FileSystemRights.ReadAndExecute</c> alone is not the mask the platform writes.</summary>
    private const FileSystemRights ReadAndNothingElse = (FileSystemRights)0x1200A9;

    /// <summary>Where the service's own creator puts the tree it makes.</summary>
    private readonly string _serviceRoot = Path.Combine(Path.GetTempPath(), "chronos-svc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestDirectory.Delete(_path);

        // The inner one first: its list is protected, so the rule added to the root is not inherited and
        // would not be enough to delete what is underneath.
        TestDirectory.Delete(Path.Combine(_serviceRoot, "data"));
        TestDirectory.Delete(_serviceRoot);
    }

    private AuthorizationRuleCollection Rules()
    {
        DataDirectory.Create(_path);

        return new DirectoryInfo(_path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
    }

    private static FileSystemAccessRule Rule(AuthorizationRuleCollection rules, WellKnownSidType sid)
    {
        var wanted = new SecurityIdentifier(sid, domainSid: null);

        return rules.Cast<FileSystemAccessRule>().Single(rule => rule.IdentityReference.Equals(wanted));
    }

    [Fact]
    public void SystemAndAdministratorsMayWrite()
    {
        var rules = Rules();

        Assert.True(Rule(rules, WellKnownSidType.LocalSystemSid).FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.True(Rule(rules, WellKnownSidType.BuiltinAdministratorsSid).FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }

    [Fact]
    public void UsersMayReadAndMayDoNothingElseAtAll()
    {
        // The whole mask, not one flag: every right nobody named is one somebody has.
        // DeleteSubdirectoriesAndFiles alone lets any user delete state.json without any right on the file,
        // and TakeOwnership and ChangePermissions let them grant themselves the rest.
        var users = Rule(Rules(), WellKnownSidType.BuiltinUsersSid);

        Assert.Equal(ReadAndNothingElse, users.FileSystemRights);
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, users.FileSystemRights);
        Assert.Equal(AccessControlType.Allow, users.AccessControlType);
    }

    [RequiresElevation("write inside a directory whose list is the one being tested")]
    public void AFileInsideAnswersUsersTheSameWay()
    {
        // The files are what matters; the directory is where they live. config.json and state.json get
        // their rights by inheritance, so a missing inheritance flag would leave every file wrong.
        DataDirectory.Create(_path);

        var file = Path.Combine(_path, "state.json");
        File.WriteAllText(file, "{}");

        var users = new FileInfo(file).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));

        Assert.Equal(ReadAndNothingElse, users.FileSystemRights);
    }

    [Fact]
    public void NobodyElseIsOnTheList()
    {
        // Exactly three rules: an upgrade may find what a wider installation left, and rights nobody
        // named are the ones that let a user rewrite the list.
        Assert.Equal(3, Rules().Count);
    }

    [Fact]
    public void InheritedRulesAreNotInForce()
    {
        // %ProgramData% grants its children rights this directory must not have; protecting the list
        // makes the three rules above the whole answer.
        DataDirectory.Create(_path);

        Assert.True(new DirectoryInfo(_path).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void ThePeopleWhoMayWriteInheritThatRightDownwards()
    {
        // state.json and config.json are what a user must not change, and they are files in this directory.
        var system = Rule(Rules(), WellKnownSidType.LocalSystemSid);

        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, system.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, system.PropagationFlags);
    }

    [Fact]
    public void CreatingTwiceIsNotAnError()
    {
        // An upgrade over an existing installation runs this again.
        DataDirectory.Create(_path);
        DataDirectory.Create(_path);
    }

    [Fact]
    public void ADirectoryWidenedByHandIsNarrowedAgainByASecondCreate()
    {
        // A fresh list rather than the directory's own, so whatever a wider installation or an ACL editor
        // granted is gone, not kept and added to.
        DataDirectory.Create(_path);

        var directory = new DirectoryInfo(_path);
        var widened = directory.GetAccessControl();
        widened.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(widened);

        DataDirectory.Create(_path);

        Assert.Equal(ReadAndNothingElse, Rule(Rules(), WellKnownSidType.BuiltinUsersSid).FileSystemRights);
        Assert.Equal(3, Rules().Count);
    }

    // Writes inside the directory it just made, and only SYSTEM and Administrators may, so an
    // unelevated run fails here rather than proving anything.
    [RequiresElevation("write inside a directory whose list is the one being tested")]
    public void TheDirectoryTheServiceMakesForItselfHasTheSameRights()
    {
        // The service creates this directory too, and %ProgramData% grants BUILTIN\Users write on
        // everything created under it. A bare CreateDirectory would leave state.json world-writable until
        // the next install. The service runs as SYSTEM and sets the list itself; both creators share the code.
        var paths = new ChronosPaths(Path.Combine(_serviceRoot, "data"), Path.Combine(_serviceRoot, "user"));

        Program.CreateDataDirectory(paths);

        var users = new DirectoryInfo(paths.DataDirectory).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));

        Assert.Equal(ReadAndNothingElse, users.FileSystemRights);
        Assert.True(new DirectoryInfo(paths.DataDirectory).GetAccessControl().AreAccessRulesProtected);

        // And the log directory under it, which the service writes into from its first line.
        Assert.True(Directory.Exists(paths.ServiceLogDirectory));
    }
}
