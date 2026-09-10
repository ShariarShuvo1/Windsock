using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Windsock.Core.Hardware;

internal sealed partial class CpuThermalProbe : IDisposable
{
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string IntelModule = "Windsock.Core.Resources.PawnIo.IntelMSR.bin";
    private const string AmdModule = "Windsock.Core.Resources.PawnIo.AMDFamily17.bin";
    private const long IntelTemperatureTarget = 0x1A2;
    private const long IntelPackageThermStatus = 0x1B1;
    private const long IntelThermStatus = 0x19C;
    private const long AmdCurrentTemperature = 0x00059800;
    private const double Coldest = 1;
    private const double Hottest = 150;
    private const int FirstZenFamily = 0x17;
    private readonly PawnIo? _channel;
    private readonly bool _intel;
    private readonly long _intelTarget;

    public CpuThermalProbe()
    {
        (string vendor, int family) = Identify();

        _intel = vendor is "GenuineIntel";

        bool amd = vendor is "AuthenticAMD";
        if (!_intel && (!amd || family < FirstZenFamily))
        {
            State = PawnIoState.Unavailable;
            Detail = "This processor is not supported";
            Explanation = amd
                ? "Windsock reads AMD temperatures on Zen and later. This chip predates it."
                : "Windsock reads processor temperatures on Intel and on AMD Zen and later.";

            return;
        }

        _channel = PawnIo.Open(_intel ? IntelModule : AmdModule, out PawnIoState state);
        State = state;

        (Detail, Explanation) = state switch
        {
            PawnIoState.Running => (null, null),
            PawnIoState.NotInstalled => (
                "PawnIO is not installed",
                "A processor temperature can only be read by kernel code. Windsock reads it "
                    + "through PawnIO, a signed driver installed separately. Without it the rest "
                    + "of the processor tile is unaffected."),
            PawnIoState.RequiresElevation => (
                "Needs elevation",
                "PawnIO admits administrators only. Run Windsock elevated to read the "
                    + "processor temperature."),
            _ => (
                "PawnIO would not answer",
                "PawnIO is installed but its driver did not accept the request. Restarting the "
                    + "machine after installing it usually settles this."),
        };
        if (_intel && State is PawnIoState.Running)
        {
            _intelTarget = _channel?.Call("ioctl_read_msr", [IntelTemperatureTarget], 1) is [long target]
                ? target
                : 0;
        }
    }

    /// <summary>Whether a reading is being taken, and if not, why not.</summary>
    public PawnIoState State { get; }

    /// <summary>Short reason shown when <see cref="State"/> is not running.</summary>
    public string? Detail { get; }

    /// <summary>The longer version of <see cref="Detail"/>, for a tooltip.</summary>
    public string? Explanation { get; }

    internal static double? IntelCelsius(long target, long status)
    {
        long tjMax = (target >> 16) & 0xFF;
        long below = (status >> 16) & 0x7F;
        if (tjMax == 0)
        {
            return null;
        }

        return Sane(tjMax - below);
    }

    internal static double? AmdCelsius(long raw)
    {
        double celsius = ((raw >> 21) & 0x7FF) * 0.125;

        if ((raw & (1 << 19)) != 0)
        {
            celsius -= 49;
        }

        return Sane(celsius);
    }

    /// <summary>The processor's temperature now, or nothing where it will not say.</summary>
    public double? Read()
    {
        if (_channel is null || State is not PawnIoState.Running)
        {
            return null;
        }

        return _intel ? ReadIntel() : ReadAmd();
    }

    public void Dispose() => _channel?.Dispose();

    private static double? Sane(double celsius) => celsius is >= Coldest and <= Hottest ? celsius : null;

    private static (string Vendor, int Family) Identify()
    {
        try
        {
            using RegistryKey? cpu = Registry.LocalMachine.OpenSubKey(CpuKey);

            string vendor = cpu?.GetValue("VendorIdentifier") as string ?? string.Empty;
            string identifier = cpu?.GetValue("Identifier") as string ?? string.Empty;

            // "AMD64 Family 25 Model 33 Stepping 2", decimal, as Windows writes it.
            Match family = FamilyPattern().Match(identifier);

            return (
                vendor.Trim(),
                family.Success ? int.Parse(family.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (string.Empty, 0);
        }
    }

    [GeneratedRegex(@"Family (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FamilyPattern();

    private double? ReadIntel()
    {
        if (_channel?.Call("ioctl_read_msr", [IntelPackageThermStatus], 1) is [long package]
            && IntelCelsius(_intelTarget, package) is { } celsius)
        {
            return celsius;
        }
        return _channel?.Call("ioctl_read_msr", [IntelThermStatus], 1) is [long core]
            ? IntelCelsius(_intelTarget, core)
            : null;
    }

    private double? ReadAmd() =>
        _channel?.Call("ioctl_read_smn", [AmdCurrentTemperature], 1) is [long raw]
            ? AmdCelsius(raw)
            : null;
}
