using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Windsock.App.Controls;

/// <summary>
/// The keyboard behaviour every table in the app shares, switched on by
/// <c>TableStyle</c>.
/// </summary>
/// <remarks>
/// The rows are one Tab stop (<c>TableStyle</c>'s rows panel and
/// <c>TableRowStyle</c>'s cells panel are both
/// <c>KeyboardNavigation.TabNavigation="Once"</c>; WPF's own cells panel is
/// <c>Local</c>, which is what walked Tab across a row): Tab reaches the table,
/// the arrow keys, Home, End and Page Up/Down move between its cells and rows
/// as the DataGrid always did, and the next Tab leaves it. Before, every cell
/// was a Tab stop of its own, 300 to 600 of them on a busy Apps page or details
/// window. Two things here keep what was reachable reachable:
/// <list type="bullet">
/// <item>A cell that holds nothing but one button (a row's menu, Close, a
/// path to show in Explorer) hands its focus to that button, so the arrow keys
/// land on the button itself and Enter or Space presses it. The arrows still
/// work from there: the grid finds the cell from the button.</item>
/// <item>The context-menu key, or Shift+F10, opens the focused row's own menu
/// (a toggle named <c>RowMenu</c> in the row), as it would a context menu.</item>
/// </list>
/// A column heading whose filter the keyboard reached says so
/// (<see cref="ShowsKeyboardFocusProperty"/>), which shows the faded funnel and
/// lights the heading; a click on the funnel focuses it too, and must leave the
/// heading as the mouse always left it.
/// </remarks>
public static class TableKeyboard
{
    /// <summary>Whether the table takes the shared keyboard behaviour.</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(TableKeyboard),
            new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>
    /// Set on a column heading while its filter has the focus because the
    /// keyboard put it there (<see cref="KeyPress"/>), never for a click.
    /// </summary>
    public static readonly DependencyProperty ShowsKeyboardFocusProperty =
        DependencyProperty.RegisterAttached(
            "ShowsKeyboardFocus",
            typeof(bool),
            typeof(TableKeyboard),
            new PropertyMetadata(false));

    /// <summary>The name of the toggle that opens a row's own menu.</summary>
    public const string RowMenuName = "RowMenu";

    public static bool GetShowsKeyboardFocus(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (bool)element.GetValue(ShowsKeyboardFocusProperty);
    }

    public static void SetShowsKeyboardFocus(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(ShowsKeyboardFocusProperty, value);
    }

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(IsEnabledProperty, value);
    }

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not DataGrid table)
        {
            return;
        }

        table.Loaded -= OnLoaded;
        table.IsVisibleChanged -= OnShown;
        table.GotKeyboardFocus -= OnFocused;
        table.LostKeyboardFocus -= OnUnfocused;
        table.PreviewKeyDown -= OnKey;

        if (e.NewValue is true)
        {
            KeyPress.Watch();
            table.Loaded += OnLoaded;
            table.IsVisibleChanged += OnShown;
            table.GotKeyboardFocus += OnFocused;
            table.LostKeyboardFocus += OnUnfocused;
            table.PreviewKeyDown += OnKey;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid table)
        {
            GroupHeadings(table);
        }
    }

    /// <summary>
    /// A table that loads hidden (the Apps page's period table, behind the
    /// live one) has no headings laid out yet: they are grouped once it shows.
    /// </summary>
    private static void OnShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is DataGrid table && e.NewValue is true)
        {
            table.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => GroupHeadings(table)));
        }
    }

    /// <summary>
    /// Makes the heading row one Tab stop, as the rows are: the headings
    /// presenter, and the cells panel it lays the headings out on (which, like
    /// a row's, WPF makes <c>Local</c>), become <c>Once</c>. Both live in the
    /// theme's DataGrid template, out of reach of a style, so they are found
    /// once the grid is laid out. The arrows move between the filters (OnKey).
    /// </summary>
    private static void GroupHeadings(DataGrid table)
    {
        foreach (DataGridColumnHeadersPresenter headings in Below<DataGridColumnHeadersPresenter>(table))
        {
            KeyboardNavigation.SetTabNavigation(headings, KeyboardNavigationMode.Once);

            foreach (DataGridCellsPanel panel in Below<DataGridCellsPanel>(headings))
            {
                KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once);
            }
        }
    }

    private static void OnFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (KeyPress.IsBeingHandled
            && e.NewFocus is DependencyObject focused
            && Ancestry.Find<DataGridColumnHeader>(focused) is { } heading)
        {
            SetShowsKeyboardFocus(heading, true);
        }

        if (e.NewFocus is DataGridCell cell
            && ReferenceEquals(e.OriginalSource, cell)
            && SoleButton(cell) is { IsVisible: true, IsEnabled: true, Focusable: true } button)
        {
            _ = button.Focus();
        }
    }

    private static void OnUnfocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OldFocus is DependencyObject left
            && Ancestry.Find<DataGridColumnHeader>(left) is { } heading
            && !(e.NewFocus is DependencyObject next && Ancestry.Find<DataGridColumnHeader>(next) == heading))
        {
            heading.ClearValue(ShowsKeyboardFocusProperty);
        }
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (sender is DataGrid table
            && Keyboard.Modifiers == ModifierKeys.None
            && e.Key is Key.Left or Key.Right or Key.Home or Key.End
            && e.OriginalSource is ToggleButton { Name: FilterName } filter
            && Ancestry.Find<DataGridColumnHeader>(filter) is not null)
        {
            e.Handled = MoveAlongHeadings(table, filter, e.Key);
            return;
        }

        bool menuKey = e.Key == Key.Apps
            || (e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift);

        if (!menuKey
            || Ancestry.Find<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row
            || Named<ToggleButton>(row, RowMenuName) is not { IsVisible: true, IsEnabled: true } menu)
        {
            return;
        }

        // Escape gives the focus back to where the keyboard was, not to the
        // menu's own button: the popup hands it to the button as it closes,
        // and this takes it on to the cell.
        if (Keyboard.FocusedElement is UIElement from && !ReferenceEquals(from, menu))
        {
            ReturnOnClose(menu, from);
        }

        menu.IsChecked = true;
        e.Handled = true;
    }

    /// <summary>The name of the funnel in a column heading (Table.xaml).</summary>
    private const string FilterName = "FilterToggle";

    /// <summary>
    /// Moves from one heading's filter to the next one shown, left or right,
    /// or to the first or last: the heading row is one Tab stop, so the arrows
    /// are how its filters are reached.
    /// </summary>
    private static bool MoveAlongHeadings(DataGrid table, ToggleButton from, Key key)
    {
        List<ToggleButton> filters = [.. table.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .OrderBy(column => column.DisplayIndex)
            .Select(column => HeadingOf(table, column))
            .Select(heading => heading is null ? null : Named<ToggleButton>(heading, FilterName))
            .OfType<ToggleButton>()
            .Where(filter => filter.IsVisible && filter.IsEnabled)];

        int at = filters.IndexOf(from);

        if (at < 0 || filters.Count == 0)
        {
            return false;
        }

        int to = key switch
        {
            Key.Left => Math.Max(0, at - 1),
            Key.Right => Math.Min(filters.Count - 1, at + 1),
            Key.Home => 0,
            _ => filters.Count - 1,
        };

        return to == at || filters[to].Focus();
    }

    private static DataGridColumnHeader? HeadingOf(DataGrid table, DataGridColumn column) =>
        Below<DataGridColumnHeader>(table).FirstOrDefault(heading => ReferenceEquals(heading.Column, column));

    /// <summary>
    /// When the row menu opened from the keyboard closes with the focus back
    /// on its button (where the popup puts it), sends the focus on to the
    /// element the keyboard was on. A click elsewhere, or a window an item
    /// opened, has the focus by then and keeps it.
    /// </summary>
    private static void ReturnOnClose(ToggleButton menu, UIElement from)
    {
        if (Below<Popup>(menu).FirstOrDefault() is not { } popup)
        {
            return;
        }

        void OnClosed(object? sender, EventArgs e)
        {
            popup.Closed -= OnClosed;

            popup.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                bool onMenu = Keyboard.FocusedElement is null || ReferenceEquals(Keyboard.FocusedElement, menu);

                if (onMenu && from.IsVisible && from.Focusable && ReferenceEquals(Window.GetWindow(from), Window.GetWindow(menu)))
                {
                    _ = from.Focus();
                }
            }));
        }

        popup.Closed += OnClosed;
    }

    /// <summary>Every element of a kind in the visual tree under a root.</summary>
    private static IEnumerable<T> Below<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);

            if (child is T found)
            {
                yield return found;
            }

            foreach (T deeper in Below<T>(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>
    /// The button a cell holds and nothing else: a cell whose content runs
    /// down through single children to a button. A cell laying out several
    /// things (a name beside a group's chevron, a publisher beside its mark)
    /// keeps the focus itself.
    /// </summary>
    private static ButtonBase? SoleButton(DependencyObject cell)
    {
        DependencyObject node = cell;

        for (int depth = 0; depth < 8; depth++)
        {
            if (VisualTreeHelper.GetChildrenCount(node) != 1)
            {
                return null;
            }

            node = VisualTreeHelper.GetChild(node, 0);

            if (node is ButtonBase button)
            {
                return button;
            }

            if (node is Panel)
            {
                return null;
            }
        }

        return null;
    }

    private static T? Named<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);

            if (child is T found && string.Equals(found.Name, name, StringComparison.Ordinal))
            {
                return found;
            }

            if (Named<T>(child, name) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
