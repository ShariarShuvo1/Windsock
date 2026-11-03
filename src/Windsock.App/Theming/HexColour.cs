using System.Windows.Media;
using Windsock.Core.Meter;

namespace Windsock.App.Theming;

/// <summary>
/// Reads a colour the meter's settings write down - #rgb, #argb, #rrggbb or
/// #aarrggbb - by hand, never through WPF's ColorConverter.
/// </summary>
/// <remarks>
/// ColorConverter accepts far more than a colour: "ContextColor &lt;uri&gt; ..."
/// makes it load a colour profile from any address, the internet or a share
/// with the person's credentials included, and "sc#..." and the rest throw
/// exceptions nobody catches. A colour in a settings or layout file comes from
/// whoever wrote the file, so it is held to <see cref="MeterLayoutFile.IsColour"/>'s
/// hex forms and read here, where the worst a bad one can do is nothing.
/// </remarks>
public static class HexColour
{
    /// <summary>Reads a written colour, or says there is none.</summary>
    public static bool TryParse(string? text, out Color colour)
    {
        colour = default;

        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> hex = text.AsSpan().Trim();

        if (hex.Length is not (4 or 5 or 7 or 9) || hex[0] != '#')
        {
            return false;
        }

        hex = hex[1..];

        foreach (char each in hex)
        {
            if (!char.IsAsciiHexDigit(each))
            {
                return false;
            }
        }

        switch (hex.Length)
        {
            case 3:
                colour = Color.FromRgb(Twice(hex[0]), Twice(hex[1]), Twice(hex[2]));
                return true;

            case 4:
                colour = Color.FromArgb(Twice(hex[0]), Twice(hex[1]), Twice(hex[2]), Twice(hex[3]));
                return true;

            case 6:
                colour = Color.FromRgb(Byte(hex[0..2]), Byte(hex[2..4]), Byte(hex[4..6]));
                return true;

            default:
                colour = Color.FromArgb(Byte(hex[0..2]), Byte(hex[2..4]), Byte(hex[4..6]), Byte(hex[6..8]));
                return true;
        }
    }

    /// <summary>A written colour as a frozen brush, or nothing.</summary>
    public static SolidColorBrush? Brush(string? text)
    {
        if (!TryParse(text, out Color colour))
        {
            return null;
        }

        SolidColorBrush brush = new(colour);
        brush.Freeze();
        return brush;
    }

    private static byte Twice(char digit)
    {
        int value = Digit(digit);
        return (byte)((value << 4) | value);
    }

    private static byte Byte(ReadOnlySpan<char> pair) => (byte)((Digit(pair[0]) << 4) | Digit(pair[1]));

    private static int Digit(char digit) => digit switch
    {
        >= '0' and <= '9' => digit - '0',
        >= 'a' and <= 'f' => digit - 'a' + 10,
        _ => digit - 'A' + 10,
    };
}
