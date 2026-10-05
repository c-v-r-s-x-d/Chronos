using Chronos.Ipc;

namespace Chronos.App;

/// <summary>
/// The applications this interface will not offer to block, checked before a command is sent. This
/// is the weaker of two checks: the service decides, and checks again before killing a process.
/// Names only; paths under the Windows directories are the service's to refuse. Keep the list to
/// what Windows or Chronos obviously requires: an extra entry here cannot be added at all, while a
/// missing one is still refused by the service.
/// </summary>
public static class ProtectedApps
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>The executable's name when the rule names something that must keep running, or null. A name, so the screen words it.</summary>
    public static string? Refuses(AppRuleMessage rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var name = Path.GetFileName(rule.Value.Trim());

        return Names.Contains(name) ? name : null;
    }
}
