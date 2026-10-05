using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

public sealed class ServiceDefinitionTests
{
    private static ServiceDefinition Definition => ServiceDefinition.Chronos(@"C:\Program Files\Chronos\Chronos.Service.exe");

    [Fact]
    public void TheNameIsTheOneTheSpecificationFixes()
    {
        // The interface, the CLI and the recovery task all address the service by this name.
        Assert.Equal("ChronosService", Definition.Name);
    }

    [Fact]
    public void ItStartsAutomaticallyAsLocalSystem()
    {
        Assert.Equal(ServiceStartMode.Automatic, Definition.StartMode);
        Assert.Null(Definition.AccountName);   // null means LocalSystem to the service manager
    }

    [Fact]
    public void ItDependsOnTheNetworkStackAndTheResolver()
    {
        Assert.Equal(["Tcpip", "Dnscache"], Definition.Dependencies);
    }

    [Fact]
    public void TheDependencyBlockIsDoubleNullTerminated()
    {
        // What CreateServiceW reads: entries separated by one NUL, the list closed by a second.
        // A single terminator makes the service manager read past the string.
        Assert.Equal("Tcpip\0Dnscache\0\0", Definition.DependencyBlock);
    }

    [Fact]
    public void ItRestartsAfterFiveSecondsThenTenThenEveryMinute()
    {
        // The failure actions as documented, word for word.
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)],
            Definition.RestartDelays);
    }

    [Fact]
    public void TheFailureCountResetsAfterADay()
    {
        // Without a reset period the third action (once a minute) becomes permanent behaviour after any two failures.
        Assert.Equal(TimeSpan.FromDays(1), Definition.FailureCountResetPeriod);
    }

    [Fact]
    public void TheImagePathIsQuotedSoASpaceInItIsNotAnArgument()
    {
        // "C:\Program Files\..." unquoted is read by the service manager as C:\Program with an
        // argument. The default install location has a space in it.
        Assert.Equal(@"""C:\Program Files\Chronos\Chronos.Service.exe""", Definition.CommandLine);
    }

    [Fact]
    public void TheFailureActionsRunEvenWhenTheServiceReportedItsOwnStop()
    {
        // fFailureActionsOnNonCrashFailures is FALSE unless set, and FALSE restarts the service only when the process vanished without reporting SERVICE_STOPPED.
        // A .NET host dying of an unhandled exception reports SERVICE_STOPPED with a non-zero exit code, so by default only taskkill would be covered.
        Assert.True(Definition.RestartOnNonCrashFailures);
    }

    [Fact]
    public void TwoDefinitionsOfTheSameServiceAreEqual()
    {
        // The generated Equals compares the lists by reference and answers false here; this keeps the assertion honest.
        Assert.Equal(ServiceDefinition.Chronos("chronos.exe"), ServiceDefinition.Chronos("chronos.exe"));
        Assert.Equal(
            ServiceDefinition.Chronos("chronos.exe").GetHashCode(),
            ServiceDefinition.Chronos("chronos.exe").GetHashCode());
    }

    [Fact]
    public void ADefinitionForAnotherPathIsNotEqual()
    {
        Assert.NotEqual(ServiceDefinition.Chronos("chronos.exe"), ServiceDefinition.Chronos("other.exe"));
    }

    [Fact]
    public void ItPrintsItselfWithoutThePath()
    {
        // A record's free ToString() lists every member, two of them full paths and a third with NULs.
        // A "{Definition}" in a log line at Information or above would write control characters into the file.
        var printed = Definition.ToString();

        Assert.Contains("ChronosService", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Program Files", printed, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', printed);
    }
}
