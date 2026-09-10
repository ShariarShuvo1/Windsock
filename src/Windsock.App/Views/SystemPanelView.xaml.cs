using System.Windows;
using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The System tab: what the machine is, and what it is doing.
/// </summary>
public partial class SystemPanelView : UserControl
{
    private readonly SystemPanelViewModel _viewModel;

    public SystemPanelView(SystemPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        IsVisibleChanged += OnVisibleChanged;
        Unloaded += (_, _) => _viewModel.Hide();
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
