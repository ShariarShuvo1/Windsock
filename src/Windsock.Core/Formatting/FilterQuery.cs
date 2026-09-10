using System.Globalization;

namespace Windsock.Core.Formatting;

/// <summary>How a filter compares a row's value with the one typed.</summary>
public enum Comparison
{
    AtLeast,

    GreaterThan,

    AtMost,

    LessThan,

    Exactly,
}

/// <summary>A parsed numeric filter: a comparison and the value to compare against.</summary>
public readonly record struct NumericFilter(Comparison Comparison, double Value)
{
    /// <summary>Whether <paramref name="candidate"/> passes this filter.</summary>
    public bool Matches(double candidate) => Comparison switch
    {
        Comparison.GreaterThan => candidate > Value,
        Comparison.AtMost => candidate <= Value,
        Comparison.LessThan => candidate < Value,
        Comparison.Exactly => Math.Abs(candidate - Value) < 0.000001,
        _ => candidate >= Value,
    };
}

/// <summary>
/// Reads the filter expressions typed into a column heading.
/// </summary>
public static class FilterQuery
{
    /// <summary>Reads a value in one column's units.</summary>
    public delegate bool ValueParser(string text, out double value);

    /// <summary>Reads a plain count, such as a number of sockets.</summary>
    public static bool Count(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Parses a filter expression using the column's own value reader.</summary>
    public static bool TryParse(string? text, ValueParser parse, out NumericFilter filter)
    {
        filter = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        Comparison comparison = Comparison.AtLeast;

        // Longest first, so ">=" is not read as ">" with a stray "=".
        foreach ((string token, Comparison meaning) in Operators)
        {
            if (trimmed.StartsWith(token, StringComparison.Ordinal))
            {
                comparison = meaning;
                trimmed = trimmed[token.Length..].TrimStart();
                break;
            }
        }

        if (!parse(trimmed, out double value) || double.IsNaN(value) || value < 0)
        {
            return false;
        }

        filter = new NumericFilter(comparison, value);
        return true;
    }

    private static readonly (string Token, Comparison Meaning)[] Operators =
    [
        (">=", Comparison.AtLeast),
        ("<=", Comparison.AtMost),
        ("==", Comparison.Exactly),
        (">", Comparison.GreaterThan),
        ("<", Comparison.LessThan),
        ("=", Comparison.Exactly),
    ];
}
