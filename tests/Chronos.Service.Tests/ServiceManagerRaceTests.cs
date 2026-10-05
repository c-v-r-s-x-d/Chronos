using System.ComponentModel;
using Chronos.Service.Setup;

namespace Chronos.Service.Tests;

/// <summary>
/// Which failed starts mean "the manager is already doing it" and which mean "this machine could not be changed".
/// Every negative here is a shape the platform actually raises: a <see cref="Win32Exception"/> with a system error code,
/// alone or inside the sentence <c>ServiceController</c> wraps it in.
/// </summary>
public sealed class ServiceManagerRaceTests
{
    /// <summary>ERROR_ACCESS_DENIED: the rights are missing, and nothing about this waits itself out.</summary>
    private const int AccessDenied = 5;

    /// <summary>ERROR_FILE_NOT_FOUND: the service executable is not where the registration says.</summary>
    private const int FileNotFound = 2;

    /// <summary>ERROR_SERVICE_DISABLED: somebody set the start type to disabled.</summary>
    private const int ServiceDisabled = 1058;

    [Theory]
    [InlineData(ServiceInterop.ERROR_SERVICE_ALREADY_RUNNING)]
    [InlineData(ServiceInterop.ERROR_SERVICE_CANNOT_ACCEPT_CTRL)]
    public void TheRaceIsTheRaceWhateverShapeItArrivesIn(int code)
    {
        // Both shapes, because both happen: the interop side raises the Win32Exception itself, and
        // ServiceController wraps it in an InvalidOperationException with a sentence of its own.
        Assert.True(ServiceManagerRace.LostToTheManager(new Win32Exception(code)));
        Assert.True(ServiceManagerRace.LostToTheManager(
            new InvalidOperationException("Cannot start service ChronosService on computer '.'.",
                new Win32Exception(code))));
    }

    [Theory]
    [InlineData(AccessDenied)]
    [InlineData(FileNotFound)]
    [InlineData(ServiceDisabled)]
    [InlineData(ServiceInterop.ERROR_SERVICE_DOES_NOT_EXIST)]
    public void AWin32FailureThatIsNotTheRaceIsNotTheRace(int code)
    {
        // The predicate's value is that it says no: each of these machines is still broken in two minutes, and calling it a race would hide the system's own description of what went wrong.
        Assert.False(ServiceManagerRace.LostToTheManager(new Win32Exception(code)));
        Assert.False(ServiceManagerRace.LostToTheManager(
            new InvalidOperationException("Cannot start service ChronosService on computer '.'.",
                new Win32Exception(code))));
    }

    [Fact]
    public void AFailureWithNoWin32CodeInItSaysNothingAboutTheManager()
    {
        // Not a shape ServiceController produces on this path; it keeps the predicate from being measured only against the one case an inner-exception lookup would get right.
        Assert.False(ServiceManagerRace.LostToTheManager(new InvalidOperationException("Access is denied.")));
        Assert.False(ServiceManagerRace.LostToTheManager(
            new InvalidOperationException("outer", new TimeoutException("inner"))));
    }

    [Fact]
    public void ThereIsNoRaceToLoseToWithoutAFailure() =>
        Assert.Throws<ArgumentNullException>(() => ServiceManagerRace.LostToTheManager(null!));
}
