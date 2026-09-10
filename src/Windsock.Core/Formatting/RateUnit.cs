namespace Windsock.Core.Formatting;

/// <summary>Which family a rate is counted in.</summary>
public enum RateFamily
{
    Bytes,
    Bits,
}

/// <summary>How far a rate is scaled up before it is displayed.</summary>
public enum RateScale
{
    Auto,

    Base,
    Kilo,
    Mega,
    Giga,
    Tera,
    Peta,
}

/// <summary>Labels and conversion factors for <see cref="RateFamily"/> and <see cref="RateScale"/>.</summary>
public static class RateUnits
{
    private const double BitStep = 1000d;
    private const double ByteStep = 1024d;
    private static readonly string[] BitLabels = ["bit/s", "kbit/s", "Mbit/s", "Gbit/s", "Tbit/s", "Pbit/s"];
    private static readonly string[] ByteLabels = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s", "PB/s"];

    private static readonly RateScale[] FixedScales =
    [
        RateScale.Base, RateScale.Kilo, RateScale.Mega,
        RateScale.Giga, RateScale.Tera, RateScale.Peta,
    ];

    /// <summary>Scales offered by a picker, Auto first.</summary>
    public static IReadOnlyList<RateScale> Scales { get; } =
        [RateScale.Auto, .. FixedScales];

    /// <summary>Families offered by a picker.</summary>
    public static IReadOnlyList<RateFamily> Families { get; } =
        [RateFamily.Bytes, RateFamily.Bits];

    internal static int MaxStep => ByteLabels.Length - 1;

    /// <summary>Label for a family and scale, for example <c>MB/s</c>.</summary>
    public static string Label(RateFamily family, RateScale scale) =>
        scale == RateScale.Auto ? "Auto" : Label(family, StepOf(scale));

    internal static string Label(RateFamily family, int step)
    {
        string[] labels = family == RateFamily.Bits ? BitLabels : ByteLabels;
        return labels[Math.Clamp(step, 0, labels.Length - 1)];
    }

    /// <summary>Short name for a family, as shown in a picker.</summary>
    public static string Name(RateFamily family) => family == RateFamily.Bits ? "Bits" : "Bytes";

    /// <summary>Short name for a scale, as shown in a picker.</summary>
    public static string Name(RateFamily family, RateScale scale) => Label(family, scale);

    internal static double BytesPerUnit(RateFamily family, int step)
    {
        if (family == RateFamily.Bits)
        {
            // One bit is an eighth of a byte, then scaled decimally.
            return Math.Pow(BitStep, step) / 8d;
        }

        return Math.Pow(ByteStep, step);
    }

    /// <summary>How many bytes per second equal one unit at this family and scale.</summary>
    public static double BytesPerUnit(RateFamily family, RateScale scale) =>
        BytesPerUnit(family, StepOf(scale));

    internal static int StepOf(RateScale scale) => scale switch
    {
        RateScale.Base => 0,
        RateScale.Kilo => 1,
        RateScale.Mega => 2,
        RateScale.Giga => 3,
        RateScale.Tera => 4,
        RateScale.Peta => 5,
        _ => 0,
    };

    internal static double StepFactor(RateFamily family) =>
        family == RateFamily.Bits ? BitStep : ByteStep;
}
