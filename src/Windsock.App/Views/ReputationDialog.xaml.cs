using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Windsock.App.Services;
using Windsock.Core.Security;

namespace Windsock.App.Views;

/// <summary>
/// Looks a program up on VirusTotal by its hash, with the person's own key,
/// when they press the button. Opening the window sends nothing.
/// </summary>
public partial class ReputationDialog : Window
{
    private readonly AppTarget _target;
    private readonly ReputationService _reputation;
    private bool _closed;
    private Uri? _page;

    private ReputationDialog(AppTarget target, ReputationService reputation)
    {
        InitializeComponent();

        _target = target;
        _reputation = reputation;

        Heading.Text = string.Concat("Look up ", target.Name, " on VirusTotal");
        PathText.Text = target.FilePath ?? "Windows will not say which file this is.";

        if (target.FilePath is null)
        {
            Status.Text = "There is no file to look up, so nothing can be sent.";
            Send.Visibility = Visibility.Collapsed;
        }
        else if (!reputation.HasKey)
        {
            Status.Text = "Looking a program up needs your own VirusTotal API key, kept on this PC for your Windows account. Add it under Settings › Data and privacy. Nothing has been sent.";
            Send.Visibility = Visibility.Collapsed;
            Settings.Visibility = Visibility.Visible;
        }
        else
        {
            Status.Text = string.Format(
                CultureInfo.CurrentCulture,
                "Only this file's SHA-256 is sent to VirusTotal, with your key - never the file, its name or where it is. {0} of 500 lookups used today.",
                reputation.UsedToday);
        }

        // A lookup still on its way when the window closes finishes on its own
        // (twenty seconds at most) and is shown to nobody.
        Closed += (_, _) => _closed = true;
        ChoosePrimary();
    }

    /// <summary>
    /// Makes the button on show that moves things on - send, open the page,
    /// add a key - the one Enter presses. Only one of them is on show at a
    /// time, and Close stays Escape.
    /// </summary>
    private void ChoosePrimary()
    {
        bool chosen = false;

        foreach (Button button in (Button[])[Send, Page, Settings])
        {
            button.IsDefault = !chosen && button.Visibility == Visibility.Visible;
            chosen |= button.IsDefault;
        }
    }

    /// <summary>Shows the window for one program.</summary>
    public static void Show(Window? owner, AppTarget target, ReputationService reputation)
    {
        ReputationDialog dialog = new(target, reputation)
        {
            Owner = owner is { IsVisible: true } ? owner : Application.Current?.MainWindow,
        };

        dialog.ShowDialog();
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_target.FilePath is not { } path)
        {
            return;
        }

        Send.IsEnabled = false;
        Status.Text = "Working out the file's hash…";

        // Closing the window stops the hashing, and the request if it has
        // gone, rather than leaving them to finish for nobody.
        using CancellationTokenSource stop = new();
        EventHandler onClosed = (_, _) => stop.Cancel();
        Closed += onClosed;

        try
        {
            string? sha = await ReputationService.HashAsync(path, stop.Token).ConfigureAwait(true);

            if (_closed)
            {
                return;
            }

            if (sha is null)
            {
                Status.Text = "The file could not be read, so nothing was sent.";
                return;
            }

            Hash.Text = sha;
            HashBox.Visibility = Visibility.Visible;
            Status.Text = "Asking VirusTotal…";

            ReputationReport report = await _reputation.LookUpAsync(sha, stop.Token).ConfigureAwait(true);

            if (_closed)
            {
                return;
            }

            Status.Text = report.AnalysedAt is { } analysed
                ? string.Format(CultureInfo.CurrentCulture, "{0} Last analysed {1:d MMM yyyy}.", report.Text, analysed.ToLocalTime())
                : report.Text;

            if (report.Outcome is ReputationOutcome.Known or ReputationOutcome.Unknown)
            {
                _page = report.Page;
                Page.Visibility = Visibility.Visible;
            }

            Send.Visibility = report.Outcome is ReputationOutcome.HeldBack or ReputationOutcome.Failed
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The window closed; nobody is waiting for the answer.
        }
        finally
        {
            Closed -= onClosed;
            Send.IsEnabled = true;
            ChoosePrimary();
        }
    }

    private void OnPage(object sender, RoutedEventArgs e)
    {
        if (_page is not null)
        {
            InsightActions.OpenLink(_page);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow is MainWindow main)
        {
            main.ShowSection("privacy");
        }

        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
