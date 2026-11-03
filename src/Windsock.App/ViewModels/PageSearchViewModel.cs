using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Navigation;

namespace Windsock.App.ViewModels;

/// <summary>One thing the search found, as its row shows it.</summary>
/// <param name="Entry">What was found.</param>
public sealed record SearchHit(SearchEntry Entry)
{
    /// <summary>What it is called.</summary>
    public string Title => Entry.Title;

    /// <summary>Where it is: the page, and the group the page is in.</summary>
    public string Where => Entry.Where;

    /// <summary>Whether it is a page, a setting or a reading, in a word.</summary>
    public string Kind => Entry.Kind switch
    {
        SearchKind.Page => "Page",
        SearchKind.Reading => "Reading",
        _ => "Setting",
    };

    /// <summary>What a screen reader says for the row.</summary>
    public string Spoken => string.IsNullOrEmpty(Where)
        ? string.Concat(Title, ", ", Kind)
        : string.Concat(Title, ", ", Kind, ", in ", Where.Replace(" › ", ", ", StringComparison.Ordinal));
}

/// <summary>
/// The search box over the sidebar: what was typed, what it found, and which
/// result the arrow keys are on.
/// </summary>
public sealed partial class PageSearchViewModel : ObservableObject
{
    /// <summary>How many results are listed at most.</summary>
    public const int MostResults = 8;

    private SearchIndex _index = new();

    /// <summary>Raised when a result is chosen, with the result; the window turns to it.</summary>
    public event EventHandler<SearchEntry>? Chosen;

    /// <summary>What was typed.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>What it found, best first.</summary>
    public ObservableCollection<SearchHit> Results { get; } = [];

    /// <summary>The result the arrow keys are on, or -1 for none.</summary>
    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    /// <summary>Whether anything has been typed.</summary>
    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    /// <summary>Whether something was typed and nothing matched it.</summary>
    public bool IsEmpty => HasQuery && Results.Count == 0;

    /// <summary>The plain empty state: what was looked for, and what can be.</summary>
    public string EmptyText => string.Concat(
        "Nothing is called “",
        Query.Trim(),
        "”. Try a page's name, a setting, or a reading such as ping, theme or erase.");

    /// <summary>A short line saying how many matched, for screen readers as the list changes.</summary>
    public string Summary => !HasQuery
        ? string.Empty
        : Results.Count switch
        {
            0 => "No results",
            1 => "1 result",
            _ => string.Concat(Results.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), " results"),
        };

    /// <summary>Takes the index to search, built by the window from its pages.</summary>
    public void Use(SearchIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        _index = index;
        Refresh();
    }

    /// <summary>Moves the highlighted result by a step, wrapping at either end.</summary>
    public void Move(int step)
    {
        if (Results.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }

        int next = SelectedIndex < 0 ? (step > 0 ? 0 : Results.Count - 1) : SelectedIndex + step;
        SelectedIndex = ((next % Results.Count) + Results.Count) % Results.Count;
    }

    /// <summary>
    /// Goes to a result - the one given, or the highlighted one, or the first
    /// - and clears the box. False when there was nothing to go to.
    /// </summary>
    public bool Choose(SearchHit? hit = null)
    {
        hit ??= SelectedIndex >= 0 && SelectedIndex < Results.Count
            ? Results[SelectedIndex]
            : Results.FirstOrDefault();

        if (hit is null)
        {
            return false;
        }

        Query = string.Empty;
        Chosen?.Invoke(this, hit.Entry);

        return true;
    }

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        Results.Clear();

        foreach (SearchEntry entry in _index.Find(Query, MostResults))
        {
            Results.Add(new SearchHit(entry));
        }

        SelectedIndex = Results.Count > 0 ? 0 : -1;

        OnPropertyChanged(nameof(HasQuery));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(Summary));
    }
}
