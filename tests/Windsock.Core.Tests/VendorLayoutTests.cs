using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windsock.Core.Hardware;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The shapes the AMD and Intel libraries expect, checked against arithmetic.
/// </summary>
public sealed class VendorLayoutTests
{
    /// <summary>
    /// A telemetry item is four bytes of flag, two enums, and an eight-byte union.
    /// </summary>
    [Fact]
    public void IntelTelemetryItem_IsTwentyFourBytes()
    {
        Assert.Equal(24, Unsafe.SizeOf<Igcl.TelemetryItem>());
    }

    /// <summary>A power supply entry is two flags and two telemetry items.</summary>
    [Fact]
    public void IntelPsuInfo_IsTwoFlagsAndTwoItems()
    {
        Assert.Equal(4 + 4 + (2 * Unsafe.SizeOf<Igcl.TelemetryItem>()), Unsafe.SizeOf<Igcl.PsuInfo>());
    }

    /// <summary>
    /// The temperature sits where the fifth item of the record should.
    /// </summary>
    [Fact]
    public void IntelTemperature_FollowsFourItemsAndTheHeader()
    {
        int header = 8;
        int item = Unsafe.SizeOf<Igcl.TelemetryItem>();

        nint offset = Marshal.OffsetOf<Igcl.Telemetry>(nameof(Igcl.Telemetry.GpuTemperature));

        Assert.Equal(header + (4 * item), (int)offset);
    }

    /// <summary>The record is long enough to be the whole thing, not a prefix.</summary>
    [Fact]
    public void IntelTelemetry_CoversEveryFieldTheLibraryWrites()
    {
        int item = Unsafe.SizeOf<Igcl.TelemetryItem>();
        int psu = Unsafe.SizeOf<Igcl.PsuInfo>();
        int expected = 8
            + (8 * item)
            + 20 + 4
            + (7 * item)
            + 20 + 4
            + item
            + (5 * psu)
            + (5 * item)
            + (9 * item);

        Assert.Equal(expected, Unsafe.SizeOf<Igcl.Telemetry>());
    }

    /// <summary>An ADL temperature is its own length followed by the figure.</summary>
    [Fact]
    public void AmdTemperature_IsSizeThenReading()
    {
        Assert.Equal(8, Unsafe.SizeOf<Adl.Temperature>());
    }

    /// <summary>
    /// A PMLog reading is a length and a supported-and-value pair per sensor.
    /// </summary>
    [Fact]
    public void AmdLogData_IsSizeThenAPairPerSensor()
    {
        Assert.Equal(4 + (256 * 2 * sizeof(int)), Unsafe.SizeOf<Adl.LogData>());
    }

    /// <summary>
    /// A machine without the vendor library gets nothing, not an exception.
    /// </summary>
    [Fact]
    public void VendorLibraries_AbsentOrPresent_NeverThrow()
    {
        using Adl? amd = Adl.Open();
        using Igcl? intel = Igcl.Open();
        Assert.True(amd is null || amd.Count > 0);
        Assert.True(intel is null || intel.Count > 0);
    }

    [Fact]
    public void VendorLibraries_AskedForACardTheyDoNotHave_SayNothing()
    {
        using Adl? amd = Adl.Open();
        using Igcl? intel = Igcl.Open();

        Assert.Null(amd?.Read(-1));
        Assert.Null(amd?.Read(int.MaxValue));
        Assert.Null(intel?.Read(-1));
        Assert.Null(intel?.Read(int.MaxValue));
    }

    /// <summary>
    /// An adapter record is nine ints around six fixed strings.
    /// </summary>
    [Fact]
    public void AmdAdapterInfo_IsNineIntsAroundSixPaths()
    {
        Assert.Equal((9 * sizeof(int)) + (6 * 256), Unsafe.SizeOf<Adl.AdapterInfo>());
    }
}
