namespace Windsock.App.ViewModels;

/// <summary>Network › Overview's household figures, from the router's own counters.</summary>
public sealed partial class SystemPanelViewModel
{
    /// <summary>
    /// The whole household's traffic, as two plates beside today's totals,
    /// shown only while reading the router's counters is switched on. Scoped
    /// with this view model, so it goes with the window.
    /// </summary>
    public HouseholdFigureViewModel Household { get; }
}
