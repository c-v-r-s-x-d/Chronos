using System.Runtime.InteropServices;
using System.Text;

namespace Chronos.Service.Apps;

/// <summary>
/// Turns device paths into drive-letter paths. A backstop: TraceEvent translates most paths itself
/// but never notices a volume attached later. The device table is cached to avoid a system call
/// per process start.
/// </summary>
public sealed class DeviceMap(Func<IReadOnlyDictionary<string, string>> readDevices)
{
    private const string DevicePrefix = @"\Device\";

    private readonly Lock _sync = new();
    private readonly HashSet<string> _unresolvable = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, string>? _devices;

    public string ToDosPath(string ntPath)
    {
        ArgumentNullException.ThrowIfNull(ntPath);

        if (!ntPath.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ntPath;
        }

        lock (_sync)
        {
            _devices ??= readDevices();

            if (TryTranslate(_devices, ntPath, out var translated))
            {
                return translated;
            }

            var prefix = DevicePrefixOf(ntPath);
            if (_unresolvable.Contains(prefix))
            {
                return ntPath;
            }

            // A miss is either a volume attached after the table was read (one refresh fixes it,
            // for every distinct device) or a device that never resolves, such as a share under
            // \Device\Mup. The unresolvable set limits the latter to one refresh.
            _devices = readDevices();

            if (TryTranslate(_devices, ntPath, out var afterRefresh))
            {
                return afterRefresh;
            }

            _unresolvable.Add(prefix);
            return ntPath;
        }
    }

    /// <summary>The device name, e.g. \Device\HarddiskVolume30 from \Device\HarddiskVolume30\games\game.exe.</summary>
    private static string DevicePrefixOf(string ntPath)
    {
        var separator = ntPath.IndexOf('\\', DevicePrefix.Length);
        return separator < 0 ? ntPath : ntPath[..separator];
    }

    public static IReadOnlyDictionary<string, string> ReadWindowsDevices()
    {
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new StringBuilder(1024);

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = $"{letter}:";
            if (QueryDosDeviceW(drive, buffer, (uint)buffer.Capacity) is not 0)
            {
                devices[buffer.ToString()] = drive;
            }
        }

        return devices;
    }

    private static bool TryTranslate(
        IReadOnlyDictionary<string, string> devices,
        string ntPath,
        out string translated)
    {
        foreach (var (device, drive) in devices)
        {
            // Compare the separator too, or HarddiskVolume3 would match HarddiskVolume30.
            if (ntPath.Length > device.Length
                && ntPath[device.Length] == '\\'
                && ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                translated = drive + ntPath[device.Length..];
                return true;
            }
        }

        translated = ntPath;
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string deviceName, StringBuilder targetPath, uint capacity);
}
