using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Windsock.Core.Hardware;

/// <summary>
/// Reads the machine's own description.
/// </summary>
public static partial class MachineProbe
{
    private const uint RawSmbios = 0x52534D42;
    private const string VersionKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const int FirstElevenBuild = 22000;

    /// <summary>Reads everything that will not change while Windsock runs.</summary>
    public static MachineFacts Read(IReadOnlyList<GraphicsFacts> graphics)
    {
        SmbiosTables firmware = Firmware();

        return new MachineFacts(
            Environment.MachineName,
            Windows(),
            firmware.Board,
            firmware.Bios,
            Booted(),
            ProcessorProbe.ReadFacts(firmware),
            firmware.Modules,
            graphics);
    }

    /// <summary>Reads the firmware's tables, or an empty set if it will not.</summary>
    public static SmbiosTables Firmware()
    {
        uint size = GetSystemFirmwareTable(RawSmbios, 0, null, 0);

        if (size == 0)
        {
            return SmbiosTables.Empty;
        }

        byte[] buffer = new byte[size];
        uint got = GetSystemFirmwareTable(RawSmbios, 0, buffer, size);

        return got == 0 || got > size
            ? SmbiosTables.Empty
            : Smbios.Read(buffer.AsSpan(0, (int)got));
    }

    private static DateTimeOffset Booted() =>
        DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static string Windows()
    {
        string name = Registry.GetValue(VersionKey, "ProductName", null) as string ?? "Windows";
        string display = Registry.GetValue(VersionKey, "DisplayVersion", null) as string ?? string.Empty;
        string build = Registry.GetValue(VersionKey, "CurrentBuild", null) as string ?? string.Empty;

        if (Environment.OSVersion.Version.Build >= FirstElevenBuild)
        {
            name = name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        string revision = Registry.GetValue(VersionKey, "UBR", null) as int? is { } patch
            ? string.Create(CultureInfo.InvariantCulture, $"{build}.{patch}")
            : build;

        if (display.Length > 0)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{name} {display}");
        }

        return revision.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{name} (build {revision})")
            : name;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(
        uint provider,
        uint table,
        [Out] byte[]? buffer,
        uint size);
}
