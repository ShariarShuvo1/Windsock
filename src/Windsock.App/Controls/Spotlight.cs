using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Windsock.App.Controls;

internal sealed class Spotlight : IDisposable
{
    private readonly List<Action> _back = [];

    private Spotlight()
    {
    }

    /// <summary>
    /// Clears the window around <paramref name="element"/>.
    /// </summary>
    public static Spotlight? Give(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (Window.GetWindow(element) is not { } window)
        {
            return null;
        }

        Spotlight room = new();
        DependencyObject child = element;
        while (!ReferenceEquals(child, window.Content) && !ReferenceEquals(child, window))
        {
            if (VisualTreeHelper.GetParent(child) is not { } parent)
            {
                break;
            }

            room.Beside(parent, child);
            child = parent;
        }
        if (window.Content is DependencyObject top)
        {
            room.Flatten(top);
            room.Unpad(top);
        }

        room.Fill(window);
        return room;
    }

    /// <summary>Puts back everything that was moved out of the way.</summary>
    public void Dispose()
    {
        for (int each = _back.Count - 1; each >= 0; each--)
        {
            _back[each]();
        }

        _back.Clear();
    }

    private void Beside(DependencyObject parent, DependencyObject keep)
    {
        Flatten(keep);
        Unpad(parent);

        if (parent is Grid grid && keep is UIElement kept)
        {
            Rows(grid, kept);
            Columns(grid, kept);
        }
        if (parent is ScrollViewer scroller)
        {
            Remember(scroller, ScrollViewer.VerticalScrollBarVisibilityProperty);
            double down = scroller.VerticalOffset;
            double across = scroller.HorizontalOffset;

            _back.Add(() => scroller.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    scroller.ScrollToVerticalOffset(down);
                    scroller.ScrollToHorizontalOffset(across);
                })));

            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        if (parent is not Panel panel)
        {
            return;
        }

        foreach (UIElement each in panel.Children)
        {
            if (!ReferenceEquals(each, keep))
            {
                Hide(each);
            }
        }
    }

    private void Flatten(DependencyObject element)
    {
        if (element is FrameworkElement box && box.Margin != default)
        {
            Remember(box, FrameworkElement.MarginProperty);
            box.Margin = default;
        }
    }

    private void Unpad(DependencyObject holder)
    {
        if (holder is Border edge)
        {
            if (edge.Padding != default)
            {
                Remember(edge, Border.PaddingProperty);
                edge.Padding = default;
            }

            return;
        }

        if (holder is Control control && control.Padding != default)
        {
            Remember(control, Control.PaddingProperty);
            control.Padding = default;
        }
    }

    private void Rows(Grid grid, UIElement keep)
    {
        int first = Grid.GetRow(keep);
        int last = first + Math.Max(1, Grid.GetRowSpan(keep)) - 1;

        for (int each = 0; each < grid.RowDefinitions.Count; each++)
        {
            RowDefinition row = grid.RowDefinitions[each];

            Remember(row, RowDefinition.HeightProperty);
            Remember(row, RowDefinition.MinHeightProperty);

            row.MinHeight = 0;
            row.Height = each == first ? new GridLength(1, GridUnitType.Star)
                : each > first && each <= last ? GridLength.Auto
                : new GridLength(0);
        }
    }

    private void Columns(Grid grid, UIElement keep)
    {
        int first = Grid.GetColumn(keep);
        int last = first + Math.Max(1, Grid.GetColumnSpan(keep)) - 1;

        for (int each = 0; each < grid.ColumnDefinitions.Count; each++)
        {
            ColumnDefinition column = grid.ColumnDefinitions[each];

            Remember(column, ColumnDefinition.WidthProperty);
            Remember(column, ColumnDefinition.MinWidthProperty);

            column.MinWidth = 0;

            // Both ends of the span, for the same reason as Rows above.
            column.Width = each == first ? new GridLength(1, GridUnitType.Star)
                : each > first && each <= last ? GridLength.Auto
                : new GridLength(0);
        }
    }

    private void Hide(UIElement element)
    {
        Remember(element, UIElement.VisibilityProperty);
        element.Visibility = Visibility.Collapsed;
    }

    private void Fill(Window window)
    {
        WindowStyle chrome = window.WindowStyle;
        ResizeMode resizing = window.ResizeMode;
        WindowState state = window.WindowState;

        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowState = WindowState.Maximized;

        _back.Add(() =>
        {
            window.WindowState = WindowState.Normal;
            window.WindowStyle = chrome;
            window.ResizeMode = resizing;
            window.WindowState = state;
        });
    }

    private void Remember(DependencyObject holder, DependencyProperty property)
    {
        if (BindingOperations.GetBindingBase(holder, property) is { } binding)
        {
            _back.Add(() => BindingOperations.SetBinding(holder, property, binding));
            return;
        }

        object was = holder.ReadLocalValue(property);

        _back.Add(() =>
        {
            if (was == DependencyProperty.UnsetValue)
            {
                holder.ClearValue(property);
                return;
            }

            holder.SetValue(property, was);
        });
    }
}
