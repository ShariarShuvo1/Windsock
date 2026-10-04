using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>The Settings panel.</summary>
public partial class SettingsPanelView : UserControl
{
    public SettingsPanelView(SettingsPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

#if STORE
        // No route tracing, so nothing sends the packets this governs. Taken
        // out of the group it sits in rather than hidden, so nothing in it is
        // left bound to a setting this edition does not have.
        if (RoutesCard.Parent is Panel group)
        {
            group.Children.Remove(RoutesCard);
        }
#endif
        Loaded += (_, _) => viewModel.Refresh();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                viewModel.Refresh();
            }
        };
    }
}
