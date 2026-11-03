using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>Usage › Data plan: each plan's cycle, the editor, and the networks.</summary>
public partial class DataPlanPanelView : UserControl
{
    public DataPlanPanelView(DataPlanPanelViewModel viewModel)
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
