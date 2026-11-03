using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>Windows Update and Delivery Optimization this month, as a band.</summary>
public partial class UpdateShareBand : UserControl
{
    public UpdateShareBand()
    {
        InitializeComponent();

        // Read when it comes into view: the Apps page's period view, not Live.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && DataContext is UpdateShareViewModel model)
            {
                model.Refresh();
            }
        };
    }
}
