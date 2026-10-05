using System.Runtime.InteropServices;
using System.Text;

namespace Chronos.Service.Tests;

internal static class ShortPath
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint capacity);

    /// <summary>The 8.3 form of an existing path, or the path itself when the volume has 8.3 names disabled.</summary>
    public static string Of(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);

        return length is 0 ? path : buffer.ToString();
    }
}
