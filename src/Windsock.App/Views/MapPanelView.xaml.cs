using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The Map page: the network map with the page's whole height.
/// </summary>
public partial class MapPanelView : UserControl
{
    private readonly MapPanelViewModel _viewModel;

    public MapPanelView(MapPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // Visibility rather than Loaded: a minimised window stays loaded. The
        // map is built while it can be seen - here, or lifted out into a window
        // of its own - and let go of otherwise, so nothing goes on watching
        // endpoints or tracing routes for a picture nobody is looking at.
        IsVisibleChanged += (_, _) => Follow();
        MapHost.DetachedChanged += (_, _) => Follow();

        Unloaded += (_, _) =>
        {
            MapHost.Recall();
            _viewModel.Hide();
        };
    }

    private void Follow()
    {
        if (IsVisible || MapHost.IsDetached)
        {
            _viewModel.Show();
            return;
        }

        _viewModel.Hide();
    }
}
