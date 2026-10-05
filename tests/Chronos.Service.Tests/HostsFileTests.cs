using System.Security.AccessControl;
using System.Security.Principal;
using Chronos.Service.Sites;

namespace Chronos.Service.Tests;

public sealed class HostsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chronos-tests", Guid.NewGuid().ToString("N"));

    public HostsFileTests() => Directory.CreateDirectory(_root);

    private string HostsPath => Path.Combine(_root, "hosts");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SystemPath_PointsAtTheWindowsHostsFile()
    {
        Assert.EndsWith(@"drivers\etc\hosts", HostsFile.SystemPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ReturnsEmptyWhenTheFileIsMissing()
    {
        Assert.Equal(string.Empty, new HostsFile(HostsPath).Read());
    }

    [Fact]
    public void WriteThenRead_KeepsTheContentExactly()
    {
        const string content = "127.0.0.1 localhost\r\n\r\n# comment\r\n";
        var file = new HostsFile(HostsPath);

        file.Write(content);

        Assert.Equal(content, file.Read());
    }

    [Fact]
    public void ReadThenWrite_KeepsNonAsciiBytesExactly()
    {
        // "# блок" in cp1251, as a hosts file edited on a Russian Windows looks. Decoding as UTF-8 turns
        // every byte into U+FFFD and the next write commits that. Bytes in, bytes out: a string
        // comparison would hide the defect.
        byte[] original = [0x23, 0x20, 0xE1, 0xEB, 0xEE, 0xEA, 0x0D, 0x0A, 0x31, 0x32, 0x37, 0x0D, 0x0A];
        File.WriteAllBytes(HostsPath, original);
        var file = new HostsFile(HostsPath);

        file.Write(file.Read());

        Assert.Equal(original, File.ReadAllBytes(HostsPath));
    }

    [Fact]
    public void ReadThenWrite_KeepsAByteOrderMarkAsBytesRatherThanInterpretingIt()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, 0x31, 0x32, 0x37, 0x0D, 0x0A];
        File.WriteAllBytes(HostsPath, original);
        var file = new HostsFile(HostsPath);

        file.Write(file.Read());

        Assert.Equal(original, File.ReadAllBytes(HostsPath));
    }

    [Fact]
    public void Write_EmitsNoByteOrderMark()
    {
        // A BOM in the hosts file is a change to a system file we have no reason to make,
        // and some tooling treats the first line as unparsable because of it.
        new HostsFile(HostsPath).Write("127.0.0.1 localhost\r\n");

        Assert.Equal((byte)'1', File.ReadAllBytes(HostsPath)[0]);
    }

    [Fact]
    public void TemporaryPath_IsAFixedNameNextToTheHostsFile()
    {
        // A random name left behind by a kill between write and replace identifies nothing and chronos
        // clean cannot remove it. Same directory, since the replace must stay on one volume.
        Assert.Equal(Path.Combine(_root, "hosts.chronos-tmp"), new HostsFile(HostsPath).TemporaryPath);
    }

    [Fact]
    public void Write_LeavesNoTemporaryFilesBehind()
    {
        new HostsFile(HostsPath).Write("127.0.0.1 localhost\r\n");

        Assert.Equal([HostsPath], Directory.GetFiles(_root));
    }

    [Fact]
    public void Write_KeepsThePermissionsOfTheReplacedFile()
    {
        // A replaced system file that comes back with the permissions of a
        // temporary file is a security regression, not a cosmetic one.
        File.WriteAllText(HostsPath, "original\r\n");
        var identity = WindowsIdentity.GetCurrent().User!;

        var info = new FileInfo(HostsPath);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ReadAttributes, AccessControlType.Allow));
        info.SetAccessControl(security);

        new HostsFile(HostsPath).Write("replaced\r\n");

        var explicitRules = new FileInfo(HostsPath)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        Assert.Contains(
            explicitRules.Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(identity)
                && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights.HasFlag(FileSystemRights.ReadAttributes));
    }

    [Fact]
    public void Write_KeepsTheAttributesOfTheReplacedFile()
    {
        File.WriteAllText(HostsPath, "original\r\n");
        File.SetAttributes(HostsPath, File.GetAttributes(HostsPath) | FileAttributes.Hidden);

        new HostsFile(HostsPath).Write("replaced\r\n");

        Assert.True(File.GetAttributes(HostsPath).HasFlag(FileAttributes.Hidden));
    }
}
