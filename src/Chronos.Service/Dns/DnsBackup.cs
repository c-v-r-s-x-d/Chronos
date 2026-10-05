using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Chronos.Service.Dns;

/// <summary>
/// The interface DNS settings as they were before the product changed them, and when they were read.
/// </summary>
public sealed record DnsBackup(DateTimeOffset SavedAt, IReadOnlyList<InterfaceDnsState> Interfaces)
{
    /// <summary>
    /// False for an empty list, a missing list, or a null interface or server list. Such a document
    /// parses, and would only fail partway through a restore.
    /// </summary>
    public bool IsUsable() =>
        Interfaces is { Count: > 0 } && Interfaces.All(state => state is { Servers: not null });
}

/// <summary>Which of the two places took the backup. One alone still allows a restore.</summary>
public enum DnsBackupSaved
{
    BothPlaces,

    /// <summary>The file took it and the registry did not.</summary>
    FileOnly,

    /// <summary>The registry took it and the file did not.</summary>
    RegistryOnly,
}

/// <summary>The registry copy of the backup; an interface so tests do not write under HKLM.</summary>
public interface IBackupMirror
{
    void Write(string json);

    /// <summary>The copy, or null when there is none.</summary>
    string? Read();

    void Clear();
}

/// <summary>
/// The backup copy kept beside the file one. HKLM, not HKCU: the service runs as LocalSystem and
/// would never find a copy in a user hive. <see cref="Clear"/> removes the value and leaves the key.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryBackupMirror : IBackupMirror
{
    public const string Key = @"SOFTWARE\Chronos";

    public const string ValueName = "DnsBackup";

    private readonly RegistryKey _root;
    private readonly string _key;

    public RegistryBackupMirror()
        : this(Registry.LocalMachine, Key)
    {
    }

    /// <summary>The same mirror against another root, for tests.</summary>
    internal RegistryBackupMirror(RegistryKey root, string key)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _root = root;
        _key = key;
    }

    public void Write(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var key = _root.CreateSubKey(_key);

        // A plain string: an expandable one would have Windows rewrite text between percent signs.
        key.SetValue(ValueName, json, RegistryValueKind.String);
    }

    internal string Location => $@"{_root.Name}\{_key}";

    public string? Read()
    {
        using var key = _root.OpenSubKey(_key, writable: false);

        // A non-string value is treated as no backup.
        return key?.GetValue(ValueName) as string;
    }

    public void Clear()
    {
        using var key = _root.OpenSubKey(_key, writable: true);

        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public enum ProductKeyRemoval
{
    NotThere,
    Removed,

    /// <summary>It still holds a value or subkey, such as a copy a restore kept.</summary>
    KeptNotEmpty,
}

/// <summary>HKLM\SOFTWARE\Chronos, which the service creates and uninstall removes when empty.</summary>
[SupportedOSPlatform("windows")]
public static class ProductKey
{
    internal static string MachineLocation => $@"{MachineRoot.Name}\{RegistryBackupMirror.Key}";

    private static RegistryKey MachineRoot => Registry.LocalMachine;

    public static ProductKeyRemoval RemoveIfEmpty() => RemoveIfEmpty(MachineRoot, RegistryBackupMirror.Key);

    internal static ProductKeyRemoval RemoveIfEmpty(RegistryKey root, string key)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using (var opened = root.OpenSubKey(key, writable: false))
        {
            if (opened is null)
            {
                return ProductKeyRemoval.NotThere;
            }

            // DeleteSubKey takes values with it, so they are checked here: a kept copy stays.
            if (opened.ValueCount > 0 || opened.SubKeyCount > 0)
            {
                return ProductKeyRemoval.KeptNotEmpty;
            }
        }

        root.DeleteSubKey(key, throwOnMissingSubKey: false);

        return ProductKeyRemoval.Removed;
    }
}
