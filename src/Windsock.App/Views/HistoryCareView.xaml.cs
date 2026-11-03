using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>Settings › Data and privacy: the history file, its copies, scheduled exports and the weekly digest.</summary>
public partial class HistoryCareView : UserControl
{
    public HistoryCareView(HistoryCareViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }
}
