using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>Network › Adapters and Wi-Fi: what this PC is connected to.</summary>
public partial class AdaptersPanelView : UserControl
{
    public AdaptersPanelView(AdaptersPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

#if STORE
        // Built without, not hidden: the Store edition has no quick fixes and
        // never reads a saved Wi-Fi password, so their controls are not left
        // in the tree for automation or a binding to find.
        Remove(FlushDnsButton);
        Remove(ShareQrButton);
        Remove(QrPanel);
#endif

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

#if STORE
    private static void Remove(System.Windows.FrameworkElement element)
    {
        if (element.Parent is Panel panel)
        {
            panel.Children.Remove(element);
        }
    }
#endif
}
