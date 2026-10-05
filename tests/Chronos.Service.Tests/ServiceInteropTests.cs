using System.Runtime.InteropServices;
using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>
/// The declarations of advapi32, pinned the way <see cref="WfpInteropTests"/> pins the WFP ones.
/// Nothing calls an advapi32 function: sizes, offsets and constants are properties of the declarations.
/// </summary>
public sealed class ServiceInteropTests
{
    [Theory]
    [InlineData(typeof(ServiceInterop.SC_ACTION), 8)]
    [InlineData(typeof(ServiceInterop.SERVICE_FAILURE_ACTIONS), 40)]
    [InlineData(typeof(ServiceInterop.SERVICE_FAILURE_ACTIONS_FLAG), 4)]
    [InlineData(typeof(ServiceInterop.SERVICE_DESCRIPTION), 8)]
    [InlineData(typeof(ServiceInterop.SERVICE_STATUS), 28)]
    public void EveryStructHasTheSizeThePlatformExpectsOnX64(Type type, int expected)
    {
        Assert.Equal(expected, Marshal.SizeOf(type));
    }

    [Theory]
    [InlineData("dwResetPeriod", 0)]
    [InlineData("lpRebootMsg", 8)]
    [InlineData("lpCommand", 16)]
    [InlineData("cActions", 24)]
    [InlineData("lpsaActions", 32)]
    public void FailureActionFieldsSitWhereThePlatformLooksForThem(string field, int expected)
    {
        // Two four-byte fields sit next to eight-byte ones, leaving two holes. They are explicit padding fields so they can be compared with the header;
        // a field added in the wrong place moves lpsaActions.
        Assert.Equal(expected, (int)Marshal.OffsetOf<ServiceInterop.SERVICE_FAILURE_ACTIONS>(field));
    }

    [Theory]
    [InlineData("dwServiceType", 0)]
    [InlineData("dwCurrentState", 4)]
    [InlineData("dwControlsAccepted", 8)]
    [InlineData("dwWin32ExitCode", 12)]
    [InlineData("dwServiceSpecificExitCode", 16)]
    [InlineData("dwCheckPoint", 20)]
    [InlineData("dwWaitHint", 24)]
    public void StatusFieldsSitWhereThePlatformLooksForThem(string field, int expected)
    {
        // The service reports its status through this structure, PRESHUTDOWN among others; the
        // layout is pinned so the declaration is checked rather than merely present.
        Assert.Equal(expected, (int)Marshal.OffsetOf<ServiceInterop.SERVICE_STATUS>(field));
    }

    [Fact]
    public void TheFailureActionIsRestartAndNotReboot()
    {
        // SC_ACTION_RESTART is 1; 2 is SC_ACTION_REBOOT, which restarts the computer. One wrong digit is the difference.
        Assert.Equal(1u, ServiceInterop.SC_ACTION_RESTART);
    }

    [Fact]
    public void TheTwoHalvesOfTheFailureConfigurationAreDifferentInfoLevels()
    {
        // The actions are info level 2 and the flag that decides when they run is info level 4:
        // two calls, and setting only the first leaves the flag at its default of FALSE.
        Assert.Equal(2u, ServiceInterop.SERVICE_CONFIG_FAILURE_ACTIONS);
        Assert.Equal(4u, ServiceInterop.SERVICE_CONFIG_FAILURE_ACTIONS_FLAG);
        Assert.Equal(1u, ServiceInterop.SERVICE_CONFIG_DESCRIPTION);
    }

    [Fact]
    public void TheRegistrationConstantsAreTheOnesCreateServiceWWants()
    {
        Assert.Equal(0x10u, ServiceInterop.SERVICE_WIN32_OWN_PROCESS);
        Assert.Equal(2u, ServiceInterop.SERVICE_AUTO_START);
        Assert.Equal(3u, ServiceInterop.SERVICE_DEMAND_START);
        Assert.Equal(4u, ServiceInterop.SERVICE_DISABLED);
        Assert.Equal(1u, ServiceInterop.SERVICE_ERROR_NORMAL);
        Assert.Equal(0xF003Fu, ServiceInterop.SC_MANAGER_ALL_ACCESS);
        Assert.Equal(0xF01FFu, ServiceInterop.SERVICE_ALL_ACCESS);
    }

    [Fact]
    public void NotInstalledIsErrorOneThousandAndSixty()
    {
        // The one error code that is an answer rather than a failure; it tells "there is no service" from "you may not ask" (5).
        Assert.Equal(1060, ServiceInterop.ERROR_SERVICE_DOES_NOT_EXIST);
    }

    [Fact]
    public void ThePreShutdownConstantsAreTheOnesTheServiceManagerSends()
    {
        // SERVICE_ACCEPT_PRESHUTDOWN is 0x100 and its control is 0x0F; neither derives from the other, and a service accepting the wrong flag is never asked.
        Assert.Equal(4u, ServiceInterop.SERVICE_RUNNING);
        Assert.Equal(0x00000001u, ServiceInterop.SERVICE_ACCEPT_STOP);
        Assert.Equal(0x00000004u, ServiceInterop.SERVICE_ACCEPT_SHUTDOWN);
        Assert.Equal(0x00000100u, ServiceInterop.SERVICE_ACCEPT_PRESHUTDOWN);
        Assert.Equal(0x0000000Fu, ServiceInterop.SERVICE_CONTROL_PRESHUTDOWN);
    }
}
