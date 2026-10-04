using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Apps;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.App.ViewModels;

/// <summary>One app in History's popover: what it moved in the bar, and who counted it.</summary>
public sealed partial class HoverAppRow : ObservableObject
{
    public HoverAppRow(AppDescription app, AppShare share)
    {
        ArgumentNullException.ThrowIfNull(app);

        App = app;
        Name = app.Name;
        Total = share.Total;
        Amount = SizeFormatter.Format(share.Total);
        HasMeasured = share.Measured > 0;
        HasWindows = share.Windows > 0;
        IsBucket = app.Kind is AppKind.System or AppKind.Unattributed;
        Source = (HasMeasured, HasWindows) switch
        {
            (true, true) => "Measured by Windsock, with some counted by Windows while Windsock was not measuring",
            (true, false) => "Measured by Windsock",
            _ => "Counted by Windows while Windsock was not measuring",
        };
    }

    /// <summary>The app.</summary>
    public AppDescription App { get; }

    /// <summary>What it is called.</summary>
    public string Name { get; }

    /// <summary>Both directions, in bytes.</summary>
    public long Total { get; }

    /// <summary>Both directions, written out.</summary>
    public string Amount { get; }

    /// <summary>Whether Windsock measured any of it, so it wears the full colour.</summary>
    public bool HasMeasured { get; }

    /// <summary>Whether Windows counted any of it, so it wears the faint one.</summary>
    public bool HasWindows { get; }

    /// <summary>Whether this is Windows' own bucket rather than something anybody runs.</summary>
    public bool IsBucket { get; }

    /// <summary>Who counted it, in words.</summary>
    public string Source { get; }

    /// <summary>Its picture, once read.</summary>
    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }
}
