using System.Globalization;
using System.Windows;
using Windsock.Core.Settings;

namespace Windsock.App.Views;

/// <summary>Shows what an imported settings file would change, and asks.</summary>
public partial class ImportSettingsDialog : Window
{
    private ImportSettingsDialog(SettingsImport read)
    {
        InitializeComponent();

        IReadOnlyList<SettingChange> changes = read.Changes;

        Summary.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0}. Windsock restarts to put {1} in place. What belongs to this PC stays as it is: its history and outages, the adapters it measures, where its window and meter stand, and everything under Data and privacy that reaches beyond it or keeps a record.",
            changes.Count == 1 ? "One setting changes" : string.Create(CultureInfo.CurrentCulture, $"{changes.Count:N0} settings change"),
            changes.Count == 1 ? "it" : "them");

        // A file that touches everything still builds only so many rows.
        SettingChangeListing listing = SettingsTransfer.Listing(changes);
        Changes.ItemsSource = listing.Shown;
        More.Text = listing.MoreText;
        More.Visibility = listing.More > 0 ? Visibility.Visible : Visibility.Collapsed;

        Notes.Text = string.Join(Environment.NewLine, read.Notes);
        Notes.Visibility = read.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Beneath.Visibility = listing.More > 0 || read.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the reader asked for the file's settings.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Puts the changes to the reader and waits for an answer.</summary>
    public static bool Ask(SettingsImport read, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(read);

        ImportSettingsDialog dialog = new(read) { Owner = owner };
        dialog.ShowDialog();

        return dialog.Confirmed;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
