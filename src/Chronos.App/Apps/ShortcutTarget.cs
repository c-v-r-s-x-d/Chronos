using System.Runtime.InteropServices;
using System.Text;
using Chronos.App.Resources;

namespace Chronos.App.Apps;

/// <summary>What came of following a shortcut.</summary>
public enum ShortcutOutcome
{
    /// <summary>A program was found and it is there.</summary>
    Resolved,

    /// <summary>The file could not be read as a shortcut at all.</summary>
    NotAShortcut,

    /// <summary>A shortcut, but not to a program: a folder, or one of Windows' own items.</summary>
    NoTarget,

    /// <summary>A shortcut to a program that is no longer on the machine.</summary>
    TargetMissing,
}

/// <summary>
/// A shortcut followed to its end. <see cref="Path"/> is the program when there is one, and
/// otherwise the file that was asked about, so a refusal can still say what it was about.
/// </summary>
public sealed record ShortcutTarget(ShortcutOutcome Outcome, string Path)
{
    public bool IsResolved => Outcome == ShortcutOutcome.Resolved;
}

/// <summary>
/// Follows a <c>.lnk</c> to the program it starts; a rule must name the program, not the shortcut.
/// Classic COM declarations: the source generators need unsafe code, which stays off.
/// </summary>
public static class Shortcut
{
    /// <summary><c>SLGP_RAWPATH</c>: no search for a moved program, since it can reach the network and hang the dialog.</summary>
    private const uint RawPath = 0x4;

    /// <summary><c>STGM_READ</c>.</summary>
    private const int Read = 0x0;

    /// <summary>The program a shortcut starts, or why it could not be followed.</summary>
    public static ShortcutTarget Resolve(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var path = file.Trim();

        string target;
        try
        {
            // Held as object: the cast asks for each interface, so the coclass need not implement them.
            object link = new ShellLink();
            ((IPersistFile)link).Load(path, Read);

            // Long paths: MAX_PATH is not the limit for a shortcut target.
            var found = new StringBuilder(short.MaxValue);
            ((IShellLinkW)link).GetPath(found, found.Capacity, IntPtr.Zero, RawPath);

            target = found.ToString().Trim();
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or InvalidCastException)
        {
            // Every way the shell declines a file means "not a shortcut". None may escape: this is
            // reached from a file dialog.
            return new ShortcutTarget(ShortcutOutcome.NotAShortcut, path);
        }

        if (target.Length == 0 || Directory.Exists(target))
        {
            // A folder or Control Panel item: it resolves but has no executable.
            return new ShortcutTarget(ShortcutOutcome.NoTarget, path);
        }

        return File.Exists(target)
            ? new ShortcutTarget(ShortcutOutcome.Resolved, target)
            : new ShortcutTarget(ShortcutOutcome.TargetMissing, target);
    }

    /// <summary>Why a shortcut was refused, in the screen's language; empty for one that worked.</summary>
    public static string Refusal(ShortcutOutcome outcome) => outcome switch
    {
        ShortcutOutcome.NotAShortcut => Strings.ShortcutNotAShortcut,
        ShortcutOutcome.NoTarget => Strings.ShortcutNoTarget,
        ShortcutOutcome.TargetMissing => Strings.ShortcutTargetMissing,
        _ => string.Empty,
    };

    /// <summary>The shell's shortcut object, <c>CLSID_ShellLink</c>.</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ShellLink
    {
    }

    /// <summary><c>IShellLinkW</c>, cut off after <c>GetPath</c>. Declaration order is the vtable order.</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int length,
            IntPtr findData,
            uint flags);
    }

    /// <summary><c>IPersistFile</c>, cut off after <c>Load</c>; the two before it only fill the vtable.</summary>
    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, int mode);
    }
}
