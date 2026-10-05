using Chronos.Core.Rules;
using Chronos.Ipc;

namespace Chronos.Service.Rules;

public sealed class WindowsProtectedAppPolicy : IProtectedAppPolicy
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "winlogon.exe",
        "csrss.exe",
        "lsass.exe",
        "services.exe",
        "svchost.exe",
        "smss.exe",
        "wininit.exe",
        "dwm.exe",
        "Chronos.Service.exe",
        "Chronos.App.exe",
        "chronos.exe",
    };

    /// <summary>Entries end in a separator so C:\WindowsApps is not read as C:\Windows.</summary>
    private static readonly string[] ProtectedDirectories =
    [
        // All of %WINDIR%, not only System32: the Start menu components live under SystemApps and WinSxS.
        WithSeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
        WithSeparator(Environment.GetFolderPath(Environment.SpecialFolder.System)),
        WithSeparator(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)),
    ];

    public ProtectionVerdict Evaluate(AppRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var fileName = Path.GetFileName(rule.Value);
        if (ProtectedNames.Contains(fileName))
        {
            return ProtectionVerdict.Protect(IpcCodes.RulesAppProtectedSystemFile);
        }

        foreach (var directory in ProtectedDirectories)
        {
            if (directory.Length > 1
                && rule.Value.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                return ProtectionVerdict.Protect(IpcCodes.RulesAppProtectedSystemDirectory);
            }
        }

        return ProtectionVerdict.Allowed;
    }

    private static string WithSeparator(string directory) =>
        directory.Length is 0 || directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
}
