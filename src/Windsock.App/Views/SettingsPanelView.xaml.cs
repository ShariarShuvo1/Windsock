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
        // No Process tab, so nothing sends the packets this governs.
        Cards.Children.Remove(RoutesCard);
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
