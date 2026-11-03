using System.Globalization;
using System.Windows.Data;

namespace Windsock.App.Converters;

/// <summary>
/// Says a table cell's dash in words, for a screen reader: "—" is what the app
/// writes where a figure is not known (never a zero), and Narrator read it as
/// "em dash". Any other text passes through as it is.
/// </summary>
/// <remarks>
/// Bound per column (<c>AutomationProperties.Name</c> in the column's cell
/// style, on the property the column shows), so the name follows the figure as
/// it changes; a binding to the cell's own text would be read once and go stale.
/// </remarks>
public sealed class SpokenDashConverter : IValueConverter
{
    /// <summary>What a dash is said as.</summary>
    public const string Unknown = "not known";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && text.Trim() is "—" or "–" ? Unknown : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
