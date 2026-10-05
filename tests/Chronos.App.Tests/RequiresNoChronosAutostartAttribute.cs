using Microsoft.Win32;

namespace Chronos.App.Tests;

/// <summary>A fact that writes to the user's Run key. It skips if a Chronos entry already exists, since that belongs to a real installation; skipping rather than failing keeps developer suites green.</summary>
public sealed class RequiresNoChronosAutostartAttribute : FactAttribute
{
    public RequiresNoChronosAutostartAttribute()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: false);

        if (key?.GetValue("Chronos") is not null)
        {
            Skip = "Chronos already starts at logon here; that entry is not this suite's to touch.";
        }
    }
}
