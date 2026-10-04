using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>One entry in a pager's rows-per-page picker.</summary>
public sealed record PageSizeOption(int Size, string Label)
{
    /// <summary>Returns the label, so automation announces it by name.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The bar under a long table: where the page on screen stands, and the ways
/// to move. It keeps no list of its own; it drives a <see cref="PagedView{T}"/>.
/// </summary>
public sealed partial class PagerViewModel : ObservableObject
{
    /// <summary>The page sizes offered.</summary>
    public static readonly int[] Sizes = [50, 100, 250, 500];

    private readonly Action<int> _goTo;
    private readonly Action<int> _resize;
    private readonly string _noun;
    private readonly string _nouns;
    private int _size;

    /// <param name="pageSize">The page size to start with.</param>
    /// <param name="goTo">Moves the list to a page.</param>
    /// <param name="resize">Changes the list's page size, and keeps the choice.</param>
    /// <param name="noun">What one row is called, for the count ("row", "outage").</param>
    /// <param name="nouns">What several are called.</param>
    public PagerViewModel(int pageSize, Action<int> goTo, Action<int> resize, string noun, string nouns)
    {
        ArgumentNullException.ThrowIfNull(goTo);
        ArgumentNullException.ThrowIfNull(resize);

        _goTo = goTo;
        _resize = resize;
        _noun = noun;
        _nouns = nouns;

        PageSizes = [.. Sizes.Select(size => new PageSizeOption(size, size.ToString("N0", CultureInfo.CurrentCulture)))];

        _size = Normalise(pageSize);
        SelectedPageSize = _size;
    }

    /// <summary>The page sizes on offer.</summary>
    public IReadOnlyList<PageSizeOption> PageSizes { get; }

    /// <summary>How many rows a page holds.</summary>
    [ObservableProperty]
    public partial int SelectedPageSize { get; set; }

    /// <summary>The page on screen, from one.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstCommand), nameof(PreviousCommand), nameof(NextCommand), nameof(LastCommand))]
    public partial int Page { get; private set; } = 1;

    /// <summary>How many pages there are.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstCommand), nameof(PreviousCommand), nameof(NextCommand), nameof(LastCommand))]
    public partial int Pages { get; private set; } = 1;

    /// <summary>The page number as typed into the box, which moves on Enter or leaving the box.</summary>
    [ObservableProperty]
    public partial string PageText { get; set; } = "1";

    /// <summary>"Rows 201–300 of 8,760", or how many rows there are when they fit one page.</summary>
    [ObservableProperty]
    public partial string RangeText { get; private set; } = string.Empty;

    /// <summary>"of 88", beside the page box.</summary>
    [ObservableProperty]
    public partial string PagesText { get; private set; } = "of 1";

    /// <summary>
    /// Whether the bar is worth showing: when there is more than the smallest
    /// page holds, since only then is there a page size or a page to choose.
    /// </summary>
    [ObservableProperty]
    public partial bool IsNeeded { get; private set; }

    /// <summary>Whether there is more than one page, so the arrows are worth showing.</summary>
    [ObservableProperty]
    public partial bool HasPages { get; private set; }

    /// <summary>Takes where the list stands now.</summary>
    /// <param name="window">The page on screen.</param>
    /// <param name="unfiltered">How many rows there are before filtering, when a filter hides some.</param>
    public void Show(PageWindow window, int? unfiltered = null)
    {
        Page = window.Page;
        Pages = window.Pages;
        PageText = window.Page.ToString(CultureInfo.CurrentCulture);
        PagesText = string.Format(CultureInfo.CurrentCulture, "of {0:N0}", window.Pages);
        HasPages = window.Pages > 1;
        IsNeeded = window.Total > Sizes[0] || window.Pages > 1;

        string matching = unfiltered is { } all && all != window.Total
            ? string.Format(CultureInfo.CurrentCulture, " ({0:N0} before filtering)", all)
            : string.Empty;

        RangeText = window.Total == 0
            ? string.Concat("No ", _nouns, matching)
            : window.Pages == 1
                ? string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}{2}", window.Total, window.Total == 1 ? _noun : _nouns, matching)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}{1} {2:N0}–{3:N0} of {4:N0}{5}",
                    char.ToUpper(_nouns[0], CultureInfo.CurrentCulture),
                    _nouns[1..],
                    window.Start + 1,
                    window.Start + window.Count,
                    window.Total,
                    matching);
    }

    partial void OnSelectedPageSizeChanged(int value)
    {
        if (value == _size || !Sizes.Contains(value))
        {
            return;
        }

        _size = value;
        _resize(value);
    }

    /// <summary>Moves to the page typed into the box, or puts the box back when it holds no page.</summary>
    [RelayCommand]
    private void GoToTyped()
    {
        if (int.TryParse(PageText, NumberStyles.Integer, CultureInfo.CurrentCulture, out int typed))
        {
            _goTo(typed);
        }

        PageText = Page.ToString(CultureInfo.CurrentCulture);
    }

    private bool CanGoBack => Page > 1;

    private bool CanGoOn => Page < Pages;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void First() => _goTo(1);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Previous() => _goTo(Page - 1);

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void Next() => _goTo(Page + 1);

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void Last() => _goTo(Pages);

    /// <summary>A remembered page size, or the default when it is not one on offer.</summary>
    public static int Normalise(int size) => Sizes.Contains(size) ? size : 100;
}
