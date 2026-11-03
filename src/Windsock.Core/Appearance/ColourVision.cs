using System.Globalization;

namespace Windsock.Core.Appearance;

/// <summary>An opaque colour, eight bits a channel, in sRGB.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>
    /// Reads <c>#RRGGBB</c> or <c>#AARRGGBB</c>, as the palettes write colours.
    /// Alpha is ignored: these are the colours as drawn on an opaque ground.
    /// </summary>
    public static Rgb Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        ReadOnlySpan<char> digits = text.AsSpan().Trim().TrimStart('#');

        if (digits.Length == 8)
        {
            digits = digits[2..];
        }

        if (digits.Length != 6 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            throw new FormatException(string.Concat("Not a colour: ", text));
        }

        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>The colour as <c>#RRGGBB</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");
}

/// <summary>A colour in CIELAB, under the D65 white.</summary>
public readonly record struct Lab(double L, double A, double B);

/// <summary>The kinds of colour vision a palette is checked against.</summary>
public enum ColourVisionKind
{
    /// <summary>Typical colour vision.</summary>
    Normal,

    /// <summary>No working red cones (protanopia).</summary>
    Protan,

    /// <summary>No working green cones (deuteranopia).</summary>
    Deutan,

    /// <summary>No working blue cones (tritanopia).</summary>
    Tritan,
}

/// <summary>
/// How colours look to people with each kind of colour blindness, and how far
/// apart two colours are, so the palettes can be checked rather than trusted.
/// </summary>
/// <remarks>
/// <para>
/// Dichromacy is simulated with the matrices of Machado, Oliveira and Fernandes
/// (2009) at severity 1.0, applied in linear sRGB - the values in colour-science's
/// <c>machado2010</c> dataset. Differences are CIEDE2000 (Sharma, Wu and Dalal,
/// 2005) in CIELAB under D65, tested against their published worked pairs.
/// About 2.3 is a just noticeable difference; for thin chart lines and small
/// fills Windsock asks for 20 or more between colours that share a chart.
/// </para>
/// <para>
/// Contrast is WCAG 2's ratio of relative luminances, from 1 to 21.
/// </para>
/// </remarks>
public static class ColourVision
{
    private static readonly double[,] Protanopia =
    {
        { 0.152286, 1.052583, -0.204868 },
        { 0.114503, 0.786281, 0.099216 },
        { -0.003882, -0.048116, 1.051998 },
    };

    private static readonly double[,] Deuteranopia =
    {
        { 0.367322, 0.860646, -0.227968 },
        { 0.280085, 0.672501, 0.047413 },
        { -0.011820, 0.042940, 0.968881 },
    };

    private static readonly double[,] Tritanopia =
    {
        { 1.255528, -0.076749, -0.178779 },
        { -0.078411, 0.930809, 0.147602 },
        { 0.004733, 0.691367, 0.303900 },
    };

    // D65 reference white, Y = 1.
    private const double WhiteX = 0.95047;
    private const double WhiteY = 1.0;
    private const double WhiteZ = 1.08883;

    /// <summary>How a colour looks with a kind of colour vision, as linear sRGB from 0 to 1.</summary>
    public static (double R, double G, double B) Simulate(Rgb colour, ColourVisionKind kind)
    {
        double r = Linear(colour.R);
        double g = Linear(colour.G);
        double b = Linear(colour.B);

        double[,]? m = kind switch
        {
            ColourVisionKind.Protan => Protanopia,
            ColourVisionKind.Deutan => Deuteranopia,
            ColourVisionKind.Tritan => Tritanopia,
            _ => null,
        };

        if (m is null)
        {
            return (r, g, b);
        }

        return (
            Math.Clamp((m[0, 0] * r) + (m[0, 1] * g) + (m[0, 2] * b), 0, 1),
            Math.Clamp((m[1, 0] * r) + (m[1, 1] * g) + (m[1, 2] * b), 0, 1),
            Math.Clamp((m[2, 0] * r) + (m[2, 1] * g) + (m[2, 2] * b), 0, 1));
    }

    /// <summary>How far apart two colours look with a kind of colour vision, in CIEDE2000.</summary>
    public static double Difference(Rgb first, Rgb second, ColourVisionKind kind = ColourVisionKind.Normal) =>
        Difference(ToLab(Simulate(first, kind)), ToLab(Simulate(second, kind)));

    /// <summary>How far apart two colours look to whoever sees them least apart, of the four kinds.</summary>
    public static double LeastDifference(Rgb first, Rgb second) =>
        Enum.GetValues<ColourVisionKind>().Min(kind => Difference(first, second, kind));

    /// <summary>A colour, given as linear sRGB, in CIELAB.</summary>
    public static Lab ToLab((double R, double G, double B) linear)
    {
        double x = (0.4124564 * linear.R) + (0.3575761 * linear.G) + (0.1804375 * linear.B);
        double y = (0.2126729 * linear.R) + (0.7151522 * linear.G) + (0.0721750 * linear.B);
        double z = (0.0193339 * linear.R) + (0.1191920 * linear.G) + (0.9503041 * linear.B);

        double fx = LabCurve(x / WhiteX);
        double fy = LabCurve(y / WhiteY);
        double fz = LabCurve(z / WhiteZ);

        return new Lab((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>A colour in CIELAB.</summary>
    public static Lab ToLab(Rgb colour) => ToLab((Linear(colour.R), Linear(colour.G), Linear(colour.B)));

    /// <summary>The CIEDE2000 difference between two CIELAB colours, with kL = kC = kH = 1.</summary>
    public static double Difference(Lab first, Lab second)
    {
        double c1 = Math.Sqrt((first.A * first.A) + (first.B * first.B));
        double c2 = Math.Sqrt((second.A * second.A) + (second.B * second.B));
        double cMean = (c1 + c2) / 2;
        double cMean7 = Math.Pow(cMean, 7);
        double g = 0.5 * (1 - Math.Sqrt(cMean7 / (cMean7 + Math.Pow(25, 7))));

        double a1 = (1 + g) * first.A;
        double a2 = (1 + g) * second.A;
        double c1p = Math.Sqrt((a1 * a1) + (first.B * first.B));
        double c2p = Math.Sqrt((a2 * a2) + (second.B * second.B));
        double h1p = Hue(first.B, a1);
        double h2p = Hue(second.B, a2);

        double dL = second.L - first.L;
        double dC = c2p - c1p;

        double dh;

        if (c1p * c2p == 0)
        {
            dh = 0;
        }
        else if (Math.Abs(h2p - h1p) <= 180)
        {
            dh = h2p - h1p;
        }
        else if (h2p - h1p > 180)
        {
            dh = h2p - h1p - 360;
        }
        else
        {
            dh = h2p - h1p + 360;
        }

        double dH = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Radians(dh / 2));

        double lMean = (first.L + second.L) / 2;
        double cMeanP = (c1p + c2p) / 2;

        double hMean;

        if (c1p * c2p == 0)
        {
            hMean = h1p + h2p;
        }
        else if (Math.Abs(h1p - h2p) <= 180)
        {
            hMean = (h1p + h2p) / 2;
        }
        else if (h1p + h2p < 360)
        {
            hMean = (h1p + h2p + 360) / 2;
        }
        else
        {
            hMean = (h1p + h2p - 360) / 2;
        }

        double t = 1
            - (0.17 * Math.Cos(Radians(hMean - 30)))
            + (0.24 * Math.Cos(Radians(2 * hMean)))
            + (0.32 * Math.Cos(Radians((3 * hMean) + 6)))
            - (0.20 * Math.Cos(Radians((4 * hMean) - 63)));

        double dTheta = 30 * Math.Exp(-Math.Pow((hMean - 275) / 25, 2));
        double cMeanP7 = Math.Pow(cMeanP, 7);
        double rC = 2 * Math.Sqrt(cMeanP7 / (cMeanP7 + Math.Pow(25, 7)));
        double lOffset = Math.Pow(lMean - 50, 2);
        double sL = 1 + (0.015 * lOffset / Math.Sqrt(20 + lOffset));
        double sC = 1 + (0.045 * cMeanP);
        double sH = 1 + (0.015 * cMeanP * t);
        double rT = -Math.Sin(Radians(2 * dTheta)) * rC;

        double termL = dL / sL;
        double termC = dC / sC;
        double termH = dH / sH;

        return Math.Sqrt((termL * termL) + (termC * termC) + (termH * termH) + (rT * termC * termH));
    }

    /// <summary>WCAG 2's relative luminance of a colour, from 0 for black to 1 for white.</summary>
    public static double Luminance(Rgb colour) =>
        (0.2126 * Linear(colour.R)) + (0.7152 * Linear(colour.G)) + (0.0722 * Linear(colour.B));

    /// <summary>WCAG 2's contrast ratio between two colours, from 1 to 21.</summary>
    public static double Contrast(Rgb first, Rgb second)
    {
        double one = Luminance(first);
        double two = Luminance(second);

        return (Math.Max(one, two) + 0.05) / (Math.Min(one, two) + 0.05);
    }

    /// <summary>
    /// The first of a list of colours that stands out from a ground by at least
    /// a contrast ratio, or, when none does, the one that stands out most.
    /// </summary>
    public static Rgb FirstReadable(IReadOnlyList<Rgb> candidates, Rgb ground, double ratio)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            throw new ArgumentException("There must be at least one colour to choose from.", nameof(candidates));
        }

        foreach (Rgb candidate in candidates)
        {
            if (Contrast(candidate, ground) >= ratio)
            {
                return candidate;
            }
        }

        return candidates.MaxBy(candidate => Contrast(candidate, ground));
    }

    /// <summary>An sRGB channel, 0 to 255, as linear light from 0 to 1.</summary>
    public static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double LabCurve(double t)
    {
        const double Delta = 6.0 / 29.0;

        return t > Delta * Delta * Delta
            ? Math.Cbrt(t)
            : (t / (3 * Delta * Delta)) + (4.0 / 29.0);
    }

    private static double Hue(double b, double a)
    {
        if (b == 0 && a == 0)
        {
            return 0;
        }

        double degrees = Math.Atan2(b, a) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
