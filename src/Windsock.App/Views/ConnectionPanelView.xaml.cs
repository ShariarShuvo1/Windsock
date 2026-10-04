using System.Windows;
using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The Connection section: whether the internet is there, how well it answers,
/// and every time it went away.
/// </summary>
public partial class ConnectionPanelView : UserControl
{
    private readonly ConnectionPanelViewModel _viewModel;

    public ConnectionPanelView(ConnectionPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // Visibility rather than Loaded, which a minimised window never loses.
        IsVisibleChanged += OnVisibleChanged;
        Unloaded += (_, _) => _viewModel.Hide();

        // Leaving the box takes the address, the same as pressing Enter.
        CustomTargetBox.LostKeyboardFocus += (_, _) =>
        {
            if (_viewModel.ApplyCustomTargetCommand.CanExecute(null))
            {
                _viewModel.ApplyCustomTargetCommand.Execute(null);
            }
        };
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _viewModel.Show();
            return;
        }

        _viewModel.Hide();
    }
}
