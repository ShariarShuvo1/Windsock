using System.Globalization;
using System.Windows;

namespace Windsock.App.Views;

/// <summary>What to do about minutes an import would land on top of.</summary>
public enum ImportChoice
{
    Cancel,

    Skip,

    Overwrite,
}

/// <summary>Asks what should happen to minutes that are already recorded.</summary>
public partial class ImportConflictDialog : Window
{
    private ImportConflictDialog(int conflicts, int total)
    {
        InitializeComponent();

        Summary.Text = string.Format(
            CultureInfo.CurrentCulture,
            conflicts == 1
                ? "{0:N0} of the {1:N0} minutes in this file is already in your history."
                : "{0:N0} of the {1:N0} minutes in this file are already in your history.",
            conflicts,
            total);
    }

    /// <summary>The choice made, once the dialog has closed.</summary>
    public ImportChoice Choice { get; private set; } = ImportChoice.Cancel;

    /// <summary>
    /// Puts the overlap to the user and waits for an answer.
    /// </summary>
    public static ImportChoice Ask(int conflicts, int total, Window? owner)
    {
        ImportConflictDialog dialog = new(conflicts, total) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Close(ImportChoice.Skip);

    private void OnOverwrite(object sender, RoutedEventArgs e) => Close(ImportChoice.Overwrite);

    private void OnCancel(object sender, RoutedEventArgs e) => Close(ImportChoice.Cancel);

    private void Close(ImportChoice choice)
    {
        Choice = choice;
        DialogResult = choice != ImportChoice.Cancel;
    }
}
