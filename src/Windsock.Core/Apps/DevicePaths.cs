using Windows.Win32;
using Windows.Win32.Foundation;

namespace Windsock.Core.Apps;

/// <summary>
/// Turns the NT device paths Windows' data usage names programs by into the
/// drive-letter paths everything else understands.
/// </summary>
/// <remarks>
/// <c>\device\harddiskvolume3\program files\app.exe</c> is how the kernel sees a
/// file; Explorer, the icon extractor and the version reader all want
/// <c>C:\program files\app.exe</c>. Windows lower-cases the whole path, which
/// is harmless: the file system does not care about case.
/// </remarks>
public sealed class DevicePaths
{
    private readonly Lock _gate = new();
    private IReadOnlyList<DriveDevice> _drives = [];
    private long _readAt = long.MinValue;

    private static readonly long Freshness = TimeSpan.FromMinutes(1).Ticks;

    /// <summary>One drive letter and the device it stands for.</summary>
    public readonly record struct DriveDevice(string Drive, string Device);

    /// <summary>
    /// The drive-letter form of a device path, or <see langword="null"/> when
    /// no mounted drive stands for its device.
    /// </summary>
    public string? ToDosPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string? found = Translate(path, Drives(refresh: false));

        // A drive plugged in since the list was read is worth one more look.
        return found ?? Translate(path, Drives(refresh: true));
    }

    /// <summary>
    /// Translates against a given list of drives; the whole of the logic, apart
    /// from asking Windows which drives there are.
    /// </summary>
    public static string? Translate(string path, IReadOnlyList<DriveDevice> drives)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(drives);

        string trimmed = path.Trim();

        if (trimmed.Length > 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':' && trimmed[2] == '\\')
        {
            return trimmed;
        }

        if (trimmed.StartsWith(@"\??\", StringComparison.Ordinal) || trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            string rest = trimmed[4..];
            return rest.Length > 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\' ? rest : null;
        }

        DriveDevice? best = null;

        foreach (DriveDevice drive in drives)
        {
            if (drive.Device.Length == 0
                || trimmed.Length <= drive.Device.Length
                || trimmed[drive.Device.Length] != '\\'
                || !trimmed.StartsWith(drive.Device, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // \Device\HarddiskVolume1 is a prefix of \Device\HarddiskVolume10;
            // the separator check above rules that out, and the longest match
            // wins where one device sits inside another.
            if (best is null || drive.Device.Length > best.Value.Device.Length)
            {
                best = drive;
            }
        }

        return best is { } match ? match.Drive + trimmed[match.Device.Length..] : null;
    }

    private IReadOnlyList<DriveDevice> Drives(bool refresh)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
            bool read = _readAt != long.MinValue;

            if (read && now - _readAt < (refresh ? TimeSpan.TicksPerSecond : Freshness))
            {
                return _drives;
            }

            _drives = ReadDrives();
            _readAt = now;
            return _drives;
        }
    }

    internal static unsafe List<DriveDevice> ReadDrives()
    {
        List<DriveDevice> drives = [];
        Span<char> target = stackalloc char[1024];

        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string drive = string.Concat(letter.ToString(), ":");
            uint length;

            fixed (char* name = drive)
            fixed (char* buffer = target)
            {
                length = PInvoke.QueryDosDevice(new PCWSTR(name), new PWSTR(buffer), (uint)target.Length);
            }

            if (length == 0)
            {
                continue;
            }

            // The answer is a list of strings ending in an empty one; the
            // first is the device the drive currently points at.
            ReadOnlySpan<char> answer = target[..(int)Math.Min(length, (uint)target.Length)];
            int end = answer.IndexOf('\0');
            string device = (end < 0 ? answer : answer[..end]).ToString();

            if (device.Length > 0)
            {
                drives.Add(new DriveDevice(drive, device));
            }
        }

        return drives;
    }
}
