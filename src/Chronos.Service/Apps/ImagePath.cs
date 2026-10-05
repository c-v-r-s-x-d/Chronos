using System.Runtime.InteropServices;
using System.Text;

namespace Chronos.Service.Apps;

/// <summary>
/// Brings a rule's path and an event's image path to one form so they can be compared.
/// </summary>
internal static class ImagePath
{
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            var full = Path.GetFullPath(expanded);

            return ToLongForm(full);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            // Return the input: one bad entry must not disable the layer.
            return trimmed;
        }
    }

    public static string FileNameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.Trim().Trim('"');
        var separator = trimmed.LastIndexOfAny(['\\', '/']);

        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }

    private static string ToLongForm(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);

        // Zero means the path does not exist, normal for a rule naming an app that is not installed.
        return length is 0 or > 1024 ? path : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint capacity);
}
