using Microsoft.Win32;

namespace Chronos.App.Startup;

/// <summary>
/// Starts the interface at logon through a value under the user's own <c>Run</c> key, which needs no
/// administrator rights. The entry is about the interface, not blocking: its absence lifts no
/// filter. The MSI owns the same entry on an installed machine, so the interface calls
/// <see cref="Ensure"/> (repair a missing entry) rather than <see cref="Enable"/> (overwrite).
/// </summary>
public sealed class Autostart
{
    /// <summary>The user's own Run key. Under HKCU, so no administrator rights are involved.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string DefaultEntry = "Chronos";

    public Autostart()
        : this(RunKeyPath, DefaultEntry)
    {
    }

    /// <summary>A key and name of the caller's choosing, so tests do not touch the real Run key.</summary>
    internal Autostart(string keyPath, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        KeyPath = keyPath;
        Name = name;
    }

    public string KeyPath { get; }

    public string Name { get; }

    /// <summary>Where this process was started from; null when the host cannot say (single-file or hosted), and then nothing is written.</summary>
    public static string? ThisExecutable => Environment.ProcessPath;

    /// <summary>The command recorded under the key, or null when there is none.</summary>
    public string? Entry
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);

            return key?.GetValue(Name) as string;
        }
    }

    public bool IsEnabled => Entry is not null;

    /// <summary>Records the entry, replacing any existing one. One value with one name, so a second start cannot leave two.</summary>
    public void Enable(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath)
            ?? throw new InvalidOperationException($@"HKCU\{KeyPath} could not be opened.");

        key.SetValue(Name, Command(executable), RegistryValueKind.String);
    }

    /// <summary>Writes the entry only if there is none, and says whether it wrote. An existing entry came from an installer and is not overwritten.</summary>
    public bool Ensure(string executable)
    {
        if (IsEnabled)
        {
            return false;
        }

        Enable(executable);

        return true;
    }

    /// <summary>Takes the entry away. Nothing there is not a failure.</summary>
    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);

        key?.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>
    /// Quoted, because a Run entry is a command line and an unquoted one stops at the first space.
    /// <c>C:\Program Files\Chronos\Chronos.App.exe</c> would be read as <c>C:\Program.exe</c>.
    /// </summary>
    private static string Command(string executable) => $"\"{executable}\"";
}
