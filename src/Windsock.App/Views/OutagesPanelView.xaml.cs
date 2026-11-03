using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// Connection › Outages: every time the internet went away.
/// </summary>
public partial class OutagesPanelView : UserControl
{
    public OutagesPanelView(OutagesPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;

        // Visibility rather than Loaded, which a minimised window never loses.
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
}
