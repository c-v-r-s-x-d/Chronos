using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Chronos.Service.Setup;

/// <summary>The autostart entry of the account running the command, under its own Run key.</summary>
/// <remarks>
/// The key and value name match <c>Chronos.App.Startup.Autostart</c>, which creates the entry at
/// first start (the installer runs as SYSTEM and has no user hive). They are repeated because that
/// type lives in a graphical assembly the CLI must not link; a test in the app suite keeps them in step.
/// Only the current user's HKCU entry is reached. Other users' entries stay, harmless, since
/// loading every profile hive would be worse.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UserAutostart : IAutostartEntry
{
    // Public so the app test suite can pin both copies together.
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string EntryName = "Chronos";

    private readonly string _keyPath;
    private readonly string _name;

    public UserAutostart()
        : this(RunKeyPath, EntryName)
    {
    }

    /// <summary>A custom key and name, so tests do not touch the real Run entry.</summary>
    internal UserAutostart(string keyPath, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _keyPath = keyPath;
        _name = name;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);

        return key?.GetValue(_name) is not null;
    }

    public void Disable()
    {
        // A missing key or value is already the wanted state.
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);

        key?.DeleteValue(_name, throwOnMissingValue: false);
    }
}
