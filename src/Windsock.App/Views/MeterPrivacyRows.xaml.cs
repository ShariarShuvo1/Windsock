using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>The taskbar meter's outside contacts on Settings › Data and privacy.</summary>
public partial class MeterPrivacyRows : UserControl
{
    public MeterPrivacyRows(MeterPrivacyViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;

        // Read again each time the page shows: the Taskbar page sets the same switches.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                viewModel.Refresh();
            }
        };
    }
}
