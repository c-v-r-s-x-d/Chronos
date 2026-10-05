using System.Runtime.InteropServices;

namespace Chronos.App.Tests;

/// <summary>The interface carries the product icon, so Explorer and its shortcuts don't show the generic one.</summary>
public sealed class ExecutableIconTests
{
    private const uint LoadAsDataFile = 0x2;
    private static readonly IntPtr GroupIcon = 14;

    [Fact]
    public void TheInterfaceCarriesAnIcon()
    {
        var module = LoadLibraryEx(typeof(App).Assembly.Location, IntPtr.Zero, LoadAsDataFile);
        Assert.NotEqual(IntPtr.Zero, module);

        try
        {
            var found = false;
            EnumResourceNames(module, GroupIcon, (_, _, _, _) => { found = true; return false; }, IntPtr.Zero);

            Assert.True(found, "Chronos.App has no icon resource.");
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
