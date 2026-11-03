using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>Connection › Speed test: tests now or on a schedule, every result kept.</summary>
public partial class SpeedTestPanelView : UserControl
{
    public SpeedTestPanelView(SpeedTestPanelViewModel viewModel)
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

        // Leaving the box takes the address, the same as pressing Enter.
        LibreSpeedBox.LostKeyboardFocus += (_, _) =>
        {
            if (viewModel.ApplyLibreSpeedCommand.CanExecute(null))
            {
                viewModel.ApplyLibreSpeedCommand.Execute(null);
            }
        };
    }
}
