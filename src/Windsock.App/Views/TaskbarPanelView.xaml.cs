using System.Windows.Controls;
using Windsock.App.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The Taskbar tab: what the meter on the Windows taskbar shows, and what it
/// looks like showing it.
/// </summary>
public partial class TaskbarPanelView : UserControl
{
    private readonly TaskbarPanelViewModel _viewModel;

    public TaskbarPanelView(TaskbarPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        TableWheel.Attach(Board);

    }
}
