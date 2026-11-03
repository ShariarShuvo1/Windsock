using System.Windows;
using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The Taskbar page: what the meter on the Windows taskbar shows, and how it
/// looks and behaves showing it.
/// </summary>
public partial class TaskbarPanelView : UserControl
{
    /// <summary>
    /// The room the board's grid needs: three places of 150 across, with the
    /// two pixels between them. Under it the board is a list.
    /// </summary>
    private const double GridWidth = (3 * 150) + 6;

    public TaskbarPanelView(TaskbarPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;
        BoardHost.SizeChanged += OnBoardResized;
    }

    /// <summary>The view model behind the page.</summary>
    public TaskbarPanelViewModel ViewModel => (TaskbarPanelViewModel)DataContext;

    /// <summary>
    /// The grid where it fits, the list where it does not. Decided by the
    /// board's own width rather than the window's, since the sidebar and the
    /// page's padding take a share that changes with the window.
    /// </summary>
    private void OnBoardResized(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < GridWidth;

        BoardGrid.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        BoardList.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }
}
