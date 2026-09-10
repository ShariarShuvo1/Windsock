using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// Wheel behaviour shared by every table in the app.
/// </summary>
public static class TableWheel
{
    private const double Sideways = 0.5;
    private const double Slack = 0.5;

    /// <summary>Gives a table the shared wheel behaviour.</summary>
    public static void Attach(DataGrid table)
    {
        ArgumentNullException.ThrowIfNull(table);
        table.PreviewMouseWheel += OnWheel;
    }

    /// <summary>
    /// Gives a strip that only scrolls sideways the shared wheel behaviour.
    /// </summary>
    public static void Attach(ScrollViewer strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        strip.PreviewMouseWheel += OnStripWheel;
    }

    /// <summary>Finds the scroller inside a control whose template owns one.</summary>
    public static ScrollViewer? Scroller(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        if (root is ScrollViewer found)
        {
            return found;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int index = 0; index < count; index++)
        {
            if (Scroller(VisualTreeHelper.GetChild(root, index)) is { } scroll)
            {
                return scroll;
            }
        }

        return null;
    }

    private static void OnStripWheel(object sender, MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (sender is not ScrollViewer strip)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && strip.ScrollableWidth > Slack)
        {
            // Up is left, the way every other sideways wheel works.
            strip.ScrollToHorizontalOffset(strip.HorizontalOffset - (e.Delta * Sideways));
            e.Handled = true;
            return;
        }
        e.Handled = true;

        if (Ancestry.Find<ScrollViewer>(Ancestry.Above(strip)) is not { } page)
        {
            return;
        }

        page.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = page,
        });
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (sender is not DependencyObject node || Scroller(node) is not { } table)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (table.ScrollableWidth <= 0)
            {
                return;
            }

            // Up is left, the way every other sideways wheel works.
            table.ScrollToHorizontalOffset(table.HorizontalOffset - (e.Delta * Sideways));
            e.Handled = true;
            return;
        }

        // The page the table is sitting on, if it is sitting on one.
        ScrollViewer? page = Ancestry.Find<ScrollViewer>(Ancestry.Above(node));

        bool down = e.Delta < 0;
        ScrollViewer? target = down
            ? First(page, table, down)
            : First(table, page, down);

        if (target is null)
        {
            return;
        }

        int notches = Math.Max(1, Math.Abs(e.Delta) / Mouse.MouseWheelDeltaForOneLine);
        int lines = Math.Max(1, SystemParameters.WheelScrollLines) * notches;

        for (int step = 0; step < lines; step++)
        {
            if (down)
            {
                target.LineDown();
            }
            else
            {
                target.LineUp();
            }
        }

        e.Handled = true;
    }

    private static ScrollViewer? First(ScrollViewer? first, ScrollViewer? then, bool down)
    {
        if (Room(first, down))
        {
            return first;
        }

        return Room(then, down) ? then : null;
    }

    private static bool Room(ScrollViewer? scroll, bool down) =>
        scroll is not null
        && (down
            ? scroll.VerticalOffset < scroll.ScrollableHeight - Slack
            : scroll.VerticalOffset > Slack);
}
