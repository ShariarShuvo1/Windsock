using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// What Connection › Status shows below its chart: the verdict for calls and
/// games, what kind of connection this is, the hop-by-hop table and the
/// report for the provider. Hosted by <see cref="ConnectionPanelView"/>.
/// </summary>
public partial class ConnectionDeeperView : UserControl
{
    public ConnectionDeeperView(ConnectionDeeperViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        if (!ConnectionDeeperViewModel.HopOffered && HopBand.Parent is Panel panel)
        {
            // The Store edition never traces a route, so the table is not
            // there at all rather than offered unable to run.
            panel.Children.Remove(HopBand);
        }

        DataContext = viewModel;

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

        PublicCustomBox.LostKeyboardFocus += (_, _) => viewModel.ApplyPublicCustomCommand.Execute(null);
        StunCustomBox.LostKeyboardFocus += (_, _) => viewModel.ApplyStunCustomCommand.Execute(null);
    }
}
