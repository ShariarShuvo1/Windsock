using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>Settings: General, Appearance, Data and privacy, Advanced and About.</summary>
public partial class SettingsPanelView : UserControl
{
    public SettingsPanelView(SettingsPanelViewModel viewModel, InsightPrivacyViewModel insight, HistoryCareView care, ConnectionToolsPrivacyRows connectionTools, MeterPrivacyRows meterContacts)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(insight);
        ArgumentNullException.ThrowIfNull(care);

        InitializeComponent();

#if STORE
        // No reverse lookups, no route tracing, no administrator mode and no
        // PawnIO in this edition. Taken out of the page they sit in rather than
        // hidden, so nothing in them is left bound to what this edition does
        // not have, before the data context is set.
        Remove(ReverseRow);
        Remove(RoutesRow);
        Remove(AdministratorBand);
        Remove(TemperatureBand);
#endif

        ViewModel = viewModel;
        DataContext = viewModel;

        // The blocklists' and VirusTotal's rows have a view model of their own.
        InsightContactRows.DataContext = insight;

        // The history file's copies, exports and digest: a band of their own, with their own view model.
        HistoryCareHost.Content = care;

        // The connection tools' outside contacts, with their own view model.
        ConnectionToolsHost.Content = connectionTools;

        // The taskbar meter's outside contacts, with their own view model.
        MeterContactsHost.Content = meterContacts;

        Loaded += (_, _) => viewModel.Refresh();

        // Forwarding is set up on the Alerts page; the row here links there. The
        // view model shares this view's scope, so nothing needs undoing.
        viewModel.AlertsPageRequested += OnAlertsPageRequested;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                viewModel.Refresh();
            }
        };
    }

    /// <summary>The settings behind the pages, for turning to one of them from a link.</summary>
    public SettingsPanelViewModel ViewModel { get; }

    private void OnAlertsPageRequested(object? sender, EventArgs e) =>
        (System.Windows.Window.GetWindow(this) as MainWindow)?.ShowSection("alerts");

#if STORE
    private static void Remove(System.Windows.FrameworkElement element)
    {
        if (element.Parent is Panel holder)
        {
            holder.Children.Remove(element);
        }
    }
#endif
}
