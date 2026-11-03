using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// Network › Activity: what waits for connections, and in the full edition
/// where apps connect, what they looked up, each day's places and Windsock's
/// firewall rules.
/// </summary>
public partial class ActivityPanelView : UserControl
{
    public ActivityPanelView(ActivityPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

#if STORE
        // The Store edition takes no kernel trace and never changes the
        // firewall, and is built without both. Their parts come out before the
        // page has a data context, so none of their bindings is tried.
        Remove(TimelinePlate);
        Remove(RulesPlate);
        Remove(TraceNotice);
        Remove(TimelineBand);
        Remove(PlacesBand);
        Remove(LookupsBand);
        Remove(FirewallBand);
#endif

        DataContext = viewModel;

        // Visibility rather than Loaded: a minimised window stays loaded.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                viewModel.Show();
                return;
            }

            viewModel.Hide();
        };

        Unloaded += (_, _) => viewModel.Hide();
    }

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
