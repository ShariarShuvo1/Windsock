using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>What a column holds, which decides how it can be filtered.</summary>
public enum ColumnKind
{
    Text,

    Identifier,

    Count,

    Rate,

    Volume,

    Duration,
}

/// <summary>
/// Everything a column heading needs: its title, whether it is shown, and the
/// filter typed into it.
/// </summary>
public abstract partial class TableColumn : ObservableObject
{
    protected TableColumn(string title, ColumnKind kind, bool numeric, bool visible)
    {
        Title = title;
        Kind = kind;
        IsNumeric = numeric;
        DescendingFirst = numeric;
        IsVisible = visible;
        DefaultVisible = visible;
    }

    /// <summary>Heading text.</summary>
    public string Title { get; }

    /// <summary>What this column holds.</summary>
    public ColumnKind Kind { get; }

    /// <summary>Prompt shown under the filter box, in this column's own terms.</summary>
    public string FilterHint => Kind switch
    {
        ColumnKind.Text => "Contains…",
        ColumnKind.Identifier => "Contains, e.g. 17 or 1700",
        ColumnKind.Count => "e.g. 4, >2, <10, =1",
        ColumnKind.Rate => "e.g. 100k, >1M, <500",
        ColumnKind.Volume => "e.g. 10M, >1.5G",
        ColumnKind.Duration => "e.g. 2h, >30m, <1d",
        _ => "Filter…",
    };

    /// <summary>Whether the column holds numbers, which are aligned right.</summary>
    public bool IsNumeric { get; }

    /// <summary>
    /// Whether clicking the heading should sort largest first.
    /// </summary>
    public bool DescendingFirst { get; set; }

    /// <summary>Alignment for the heading, matching the cells beneath it.</summary>
    public HorizontalAlignment Alignment => IsNumeric ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>Whether the column is shown by default.</summary>
    public bool DefaultVisible { get; }

    /// <summary>Whether the column is shown. Bound to the column chooser.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    /// <summary>The filter typed into this column's header.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>Whether this column's filter popover is open.</summary>
    [ObservableProperty]
    public partial bool IsFilterOpen { get; set; }

    /// <summary>Whether a filter is currently narrowing this column.</summary>
    public bool HasFilter => !string.IsNullOrWhiteSpace(Filter);

    /// <summary>Whether the column is in the state it started in.</summary>
    public bool IsDefault => !HasFilter && IsVisible == DefaultVisible;

    /// <summary>Raised when the filter or the visibility changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Clears the filter without closing the popover.</summary>
    [RelayCommand]
    public void ClearFilter() => Filter = string.Empty;

    /// <summary>Returns the column to how it started.</summary>
    public void Reset()
    {
        Filter = string.Empty;
        IsVisible = DefaultVisible;
    }

    /// <summary>Returns the title, so automation announces the column by name.</summary>
    public override string ToString() => Title;

    /// <summary>How this column's kind of value is read out of what was typed.</summary>
    protected FilterQuery.ValueParser Reader => Kind switch
    {
        ColumnKind.Rate or ColumnKind.Volume => RateThreshold.TryParse,
        ColumnKind.Duration => DurationThreshold.TryParse,
        _ => FilterQuery.Count,
    };

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(HasFilter));
        OnPropertyChanged(nameof(IsDefault));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDefault));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// A column over a particular kind of row, which knows how to read a value out
/// of one and decide whether it survives the filter.
/// </summary>
public sealed class TableColumn<TRow> : TableColumn
{
    private readonly Func<TRow, string>? _text;
    private readonly Func<TRow, double>? _number;

    internal TableColumn(
        string title,
        ColumnKind kind,
        Func<TRow, string>? text,
        Func<TRow, double>? number,
        bool visible)
        : base(title, kind, number is not null, visible)
    {
        _text = text;
        _number = number;
    }

    /// <summary>Whether <paramref name="row"/> survives this column's filter.</summary>
    public bool Matches(TRow row)
    {
        if (!HasFilter)
        {
            return true;
        }

        if (_number is null)
        {
            return _text!(row).Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return !FilterQuery.TryParse(Filter, Reader, out NumericFilter filter) || filter.Matches(_number(row));
    }
}

/// <summary>
/// Builds columns.
/// </summary>
public static class Column
{
    /// <summary>Creates a column of words, filtered by what they contain.</summary>
    public static TableColumn<TRow> Text<TRow>(
        string title,
        Func<TRow, string> value,
        bool visible = true) =>
        new(title, ColumnKind.Text, value, null, visible);

    /// <summary>Creates a column of identifiers, filtered by what they contain.</summary>
    public static TableColumn<TRow> Identifier<TRow>(
        string title,
        Func<TRow, string> value,
        bool visible = true) =>
        new(title, ColumnKind.Identifier, value, null, visible);

    /// <summary>Creates a column of numbers, compared against what is typed.</summary>
    public static TableColumn<TRow> Number<TRow>(
        string title,
        ColumnKind kind,
        Func<TRow, double> value,
        bool visible = true) =>
        new(title, kind, null, value, visible);
}
