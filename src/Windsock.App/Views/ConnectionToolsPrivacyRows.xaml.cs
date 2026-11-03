using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>The connection tools' switches on Settings › Data and privacy.</summary>
public partial class ConnectionToolsPrivacyRows : UserControl
{
    public ConnectionToolsPrivacyRows(ConnectionToolsPrivacyViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;

        // Read again each time the page shows: the Connection pages set the same switches.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                viewModel.Refresh();
            }
        };
    }
}
