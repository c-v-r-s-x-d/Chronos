using Chronos.Service.Apps;

namespace Chronos.Service.Tests;

public sealed class DeviceMapTests
{
    private static Dictionary<string, string> OneVolume() => new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\Device\HarddiskVolume3"] = @"C:",
    };

    [Fact]
    public void ToDosPath_ReplacesTheDeviceWithItsDriveLetter()
    {
        var map = new DeviceMap(() => OneVolume());

        Assert.Equal(@"C:\Windows\notepad.exe", map.ToDosPath(@"\Device\HarddiskVolume3\Windows\notepad.exe"));
    }

    [Fact]
    public void ToDosPath_LeavesAPathThatIsAlreadyDosAlone()
    {
        var map = new DeviceMap(() => OneVolume());

        Assert.Equal(@"D:\games\game.exe", map.ToDosPath(@"D:\games\game.exe"));
    }

    [Fact]
    public void ToDosPath_DoesNotMatchALongerDeviceNameByPrefix()
    {
        // \Device\HarddiskVolume30 must not be rewritten by the entry for \Device\HarddiskVolume3.
        var map = new DeviceMap(() => OneVolume());

        const string other = @"\Device\HarddiskVolume30\games\game.exe";
        Assert.Equal(other, map.ToDosPath(other));
    }

    [Fact]
    public void ToDosPath_RefreshesOnceWhenTheDeviceIsUnknown()
    {
        // A volume attached after the cache was built must still resolve.
        var reads = 0;
        var map = new DeviceMap(() =>
        {
            reads++;
            return reads is 1
                ? OneVolume()
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"\Device\HarddiskVolume3"] = @"C:",
                    [@"\Device\HarddiskVolume9"] = @"E:",
                };
        });

        Assert.Equal(@"C:\a.exe", map.ToDosPath(@"\Device\HarddiskVolume3\a.exe"));
        Assert.Equal(@"E:\b.exe", map.ToDosPath(@"\Device\HarddiskVolume9\b.exe"));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void ToDosPath_DoesNotRefreshRepeatedlyForADeviceThatStaysUnknown()
    {
        // A device we cannot resolve must not turn every process start into a system call.
        var reads = 0;
        var map = new DeviceMap(() =>
        {
            reads++;
            return OneVolume();
        });

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(@"\Device\Unknown\x.exe", map.ToDosPath(@"\Device\Unknown\x.exe"));
        }

        Assert.Equal(2, reads);
    }

    [Fact]
    public void ToDosPath_RefreshesAgainForASecondDistinctNewVolume()
    {
        // More than one volume can be attached over a long-running service's lifetime, not just one.
        var reads = 0;
        var map = new DeviceMap(() =>
        {
            reads++;
            return reads switch
            {
                1 => OneVolume(),
                2 => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"\Device\HarddiskVolume3"] = @"C:",
                    [@"\Device\HarddiskVolume9"] = @"E:",
                },
                _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"\Device\HarddiskVolume3"] = @"C:",
                    [@"\Device\HarddiskVolume9"] = @"E:",
                    [@"\Device\HarddiskVolume11"] = @"F:",
                },
            };
        });

        Assert.Equal(@"E:\a.exe", map.ToDosPath(@"\Device\HarddiskVolume9\a.exe"));
        Assert.Equal(@"F:\b.exe", map.ToDosPath(@"\Device\HarddiskVolume11\b.exe"));
        Assert.Equal(3, reads);
    }

    [Fact]
    public void ToDosPath_CostsOneRefreshPerDistinctUnresolvableDevice()
    {
        // Two different unresolvable devices (e.g. two distinct network shares) must each
        // cost exactly one refresh, never more, and never fewer.
        var reads = 0;
        var map = new DeviceMap(() =>
        {
            reads++;
            return OneVolume();
        });

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(@"\Device\Alpha\x.exe", map.ToDosPath(@"\Device\Alpha\x.exe"));
            Assert.Equal(@"\Device\Beta\y.exe", map.ToDosPath(@"\Device\Beta\y.exe"));
        }

        Assert.Equal(3, reads);
    }

    [Fact]
    public void ReadWindowsDevices_MapsAtLeastTheSystemDrive()
    {
        // Reading the device table needs no privileges and changes nothing.
        var devices = DeviceMap.ReadWindowsDevices();

        Assert.Contains(devices, entry => entry.Value.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
        Assert.All(devices, entry => Assert.StartsWith(@"\Device\", entry.Key, StringComparison.OrdinalIgnoreCase));
    }
}
