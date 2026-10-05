using System.Reflection;
using System.Runtime.InteropServices;

namespace Chronos.Service.Tests;

/// <summary>The programs carry the product icon, so Explorer and the shortcuts don't show the generic one.</summary>
public sealed class ExecutableIconTests
{
    [Theory]
    [InlineData(typeof(Program))]
    [InlineData(typeof(Chronos.Cli.Commands.CleanCommand))]
    public void TheProgramCarriesAnIcon(Type inProgram) =>
        Assert.True(Win32Icon.Exists(inProgram.Assembly), $"{inProgram.Assembly.GetName().Name} has no icon resource.");
}

internal static class Win32Icon
{
    private const uint LoadAsDataFile = 0x2;
    private static readonly IntPtr GroupIcon = 14;

    public static bool Exists(Assembly assembly)
    {
        var module = LoadLibraryEx(assembly.Location, IntPtr.Zero, LoadAsDataFile);
        Assert.NotEqual(IntPtr.Zero, module);

        try
        {
            var found = false;
            EnumResourceNames(module, GroupIcon, (_, _, _, _) => { found = true; return false; }, IntPtr.Zero);

            return found;
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr param);
}
