using System.Globalization;
using System.Windows;
using Windsock.Core.History;

namespace Windsock.App.Views;

/// <summary>
/// Asks before the history is deleted, and says how much of it there is.
/// </summary>
public partial class EraseHistoryDialog : Window
{
    private EraseHistoryDialog(UsageExtent extent, UsageSummary summary)
    {
        InitializeComponent();

        Amount.Text = Describe(extent, summary);
        Keeping.Visibility = extent.HasData ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the reader asked for the history to go.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>
    /// Puts the deletion to the reader and waits for an answer.
    /// </summary>
    public static bool Ask(UsageExtent extent, UsageSummary summary, Window? owner)
    {
        EraseHistoryDialog dialog = new(extent, summary) { Owner = owner };
        dialog.ShowDialog();

        return dialog.Confirmed;
    }

    private static string Describe(UsageExtent extent, UsageSummary summary)
    {
        if (!extent.HasData || summary.Minutes <= 0)
        {
            return "There is nothing recorded yet, so there is nothing to delete.";
        }

        string minutes = string.Format(
            CultureInfo.CurrentCulture,
            summary.Minutes == 1 ? "{0:N0} minute is recorded" : "{0:N0} minutes are recorded",
            summary.Minutes);

        if (extent.First is not { } first || extent.Last is not { } last)
        {
            return minutes + ".";
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}, from {1:d MMMM yyyy} to {2:d MMMM yyyy}.",
            minutes,
            first.LocalDateTime,
            last.LocalDateTime);
    }

    private void OnKeep(object sender, RoutedEventArgs e) => Answer(false);

    private void OnDelete(object sender, RoutedEventArgs e) => Answer(true);

    private void Answer(bool confirmed)
    {
        Confirmed = confirmed;
        DialogResult = confirmed;
    }
}
