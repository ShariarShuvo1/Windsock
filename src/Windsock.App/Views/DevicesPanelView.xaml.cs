using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// Network › Home devices: what is connected at home, and the router's account
/// of itself.
/// </summary>
public partial class DevicesPanelView : UserControl
{
    public DevicesPanelView(DevicesPanelViewModel viewModel)
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

    /// <summary>The name box takes the keyboard as soon as it appears, with its text chosen, so typing replaces it.</summary>
    private void OnRenameBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
        {
            box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                box.Focus();
                box.SelectAll();
            }));
        }
    }
}
