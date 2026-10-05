using System.Runtime.Versioning;
using System.Security.Principal;

namespace Chronos.Cli.Commands;

/// <summary>Checks for administrator rights up front, before a command half-installs itself.</summary>
public static class Administrator
{
    [SupportedOSPlatform("windows")]
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int Refuse(string command, TextWriter error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(error);

        error.WriteLine($"'{command}' changes the whole machine and needs administrator rights.");
        error.WriteLine("Run it again from a terminal opened with 'Run as administrator'.");

        return Steps.NotElevated;
    }
}
