using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// A drop-down that opens on click.
/// </summary>
public sealed class Picker : ComboBox
{
    private const int LinesPerNotch = 3;
    private ScrollViewer? _list;
    private Window? _host;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        // An editable combo needs its click to land in the text box instead.
        if (IsEditable || e.Handled)
        {
            return;
        }
        if (e.OriginalSource is DependencyObject source && ContainerFromElement(source) is not null)
        {
            return;
        }

        IsDropDownOpen = !IsDropDownOpen;
        Focus();

        e.Handled = true;
    }

    /// <summary>
    /// Takes the wheel for as long as the list is open.
    /// </summary>
    protected override void OnDropDownOpened(EventArgs e)
    {
        base.OnDropDownOpened(e);

        _host = Window.GetWindow(this);

        if (_host is not null)
        {
            _host.PreviewMouseWheel += OnWheel;
        }
    }

    /// <summary>Gives the wheel back, and forgets a list that is about to go.</summary>
    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);

        if (_host is not null)
        {
            _host.PreviewMouseWheel -= OnWheel;
            _host = null;
        }

        _list = null;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || !IsDropDownOpen)
        {
            return;
        }

        if (List() is { ScrollableHeight: > 0 } list)
        {
            double notches = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
            list.ScrollToVerticalOffset(list.VerticalOffset - (notches * LinesPerNotch));
        }
        e.Handled = true;
    }

    private ScrollViewer? List()
    {
        if (_list is not null)
        {
            return _list;
        }
        DependencyObject? root = GetTemplateChild("PART_Popup") is Popup { Child: { } child }
            ? child
            : this;

        _list = Descendant(root);

        return _list;
    }

    private static ScrollViewer? Descendant(DependencyObject node)
    {
        if (node is ScrollViewer found)
        {
            return found;
        }

        int children = VisualTreeHelper.GetChildrenCount(node);

        for (int index = 0; index < children; index++)
        {
            if (Descendant(VisualTreeHelper.GetChild(node, index)) is { } scroller)
            {
                return scroller;
            }
        }
        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is Popup { Child: { } content } && Descendant(content) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }
}
