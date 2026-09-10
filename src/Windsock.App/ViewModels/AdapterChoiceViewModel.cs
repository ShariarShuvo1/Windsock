using CommunityToolkit.Mvvm.ComponentModel;

namespace Windsock.App.ViewModels;

/// <summary>One adapter in the settings list, ticked or not.</summary>
public sealed partial class AdapterChoiceViewModel : ObservableObject
{
    public AdapterChoiceViewModel(ulong id, string label, bool selected)
    {
        Id = id;
        Label = label;
        IsSelected = selected;
    }

    /// <summary>The interface LUID.</summary>
    public ulong Id { get; }

    /// <summary>What the adapter is called, and whether it is connected.</summary>
    public string Label { get; }

    /// <summary>Whether this adapter is being counted.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>
    /// Whether this tick is the only one left and so cannot be removed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUntick))]
    public partial bool IsLocked { get; set; }

    /// <summary>Whether the tick can still be removed.</summary>
    public bool CanUntick => !IsLocked;

    /// <summary>Raised when the tick changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Returns the label, so automation announces it by name.</summary>
    public override string ToString() => Label;

    partial void OnIsSelectedChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}
