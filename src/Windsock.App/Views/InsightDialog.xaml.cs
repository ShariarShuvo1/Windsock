using System.Windows;
using System.Windows.Automation;
using FluentIcons.Common;

namespace Windsock.App.Views;

/// <summary>
/// Asks before an action that cannot be taken back, and tells how one went.
/// </summary>
public partial class InsightDialog : Window
{
    private InsightDialog(string title, string heading, string body, IReadOnlyList<string> details, string safe, string? act, bool warning)
    {
        InitializeComponent();

        Title = title;
        Heading.Text = heading;
        Body.Text = body;
        Body.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
        Details.ItemsSource = details;
        DetailsBox.Visibility = details.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        Safe.Content = safe;
        AutomationProperties.SetName(Safe, safe);

        if (act is null)
        {
            Act.Visibility = Visibility.Collapsed;
        }
        else
        {
            Act.Content = act;
            AutomationProperties.SetName(Act, act);
        }

        if (warning)
        {
            Mark.Symbol = Symbol.Warning;
            Mark.SetResourceReference(ForegroundProperty, "HotBrush");
        }
    }

    /// <summary>Whether the reader chose the action.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Puts an action to the reader; true when they chose it.</summary>
    /// <param name="owner">The window it belongs to.</param>
    /// <param name="title">The dialog's title bar.</param>
    /// <param name="heading">The question, in a few words.</param>
    /// <param name="body">What happens, in a sentence or two.</param>
    /// <param name="details">Exactly what it applies to, a line each.</param>
    /// <param name="act">The button that acts, naming what it does.</param>
    /// <param name="keep">The button that leaves things as they are.</param>
    public static bool Ask(Window? owner, string title, string heading, string body, IReadOnlyList<string> details, string act, string keep = "Cancel")
    {
        InsightDialog dialog = new(title, heading, body, details, keep, act, warning: true) { Owner = Usable(owner) };
        dialog.ShowDialog();
        return dialog.Confirmed;
    }

    /// <summary>Says how something went.</summary>
    public static void Tell(Window? owner, string title, string heading, string body, IReadOnlyList<string> details, bool warning = false)
    {
        InsightDialog dialog = new(title, heading, body, details, "Close", null, warning) { Owner = Usable(owner) };
        dialog.ShowDialog();
    }

    /// <summary>An owner a dialog may sit on: one that is showing.</summary>
    private static Window? Usable(Window? owner) =>
        owner is { IsVisible: true } ? owner : Application.Current?.MainWindow is { IsVisible: true } main ? main : null;

    private void OnSafe(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void OnAct(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }
}
