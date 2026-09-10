using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// Dismisses a popup on a click outside it, without letting it take the mouse.
/// </summary>
public static class Flyout
{
    private static readonly List<Popup> Showing = [];

    /// <summary>Whether a click outside this popup should close it.</summary>
    public static readonly DependencyProperty DismissOnOutsideClickProperty =
        DependencyProperty.RegisterAttached(
            "DismissOnOutsideClick",
            typeof(bool),
            typeof(Flyout),
            new PropertyMetadata(false, OnDismissChanged));

    public static bool GetDismissOnOutsideClick(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (bool)element.GetValue(DismissOnOutsideClickProperty);
    }

    public static void SetDismissOnOutsideClick(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(DismissOnOutsideClickProperty, value);
    }

    private static void OnDismissChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Popup popup)
        {
            return;
        }

        popup.Opened -= OnOpened;
        popup.Closed -= OnClosed;

        if (e.NewValue is true)
        {
            popup.Opened += OnOpened;
            popup.Closed += OnClosed;
        }
    }

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup)
        {
            return;
        }

        if (!Showing.Contains(popup))
        {
            Showing.Add(popup);
        }

        if (Host(popup) is not { } window)
        {
            return;
        }
        Detach(window);
        window.PreviewMouseDown += OnPressed;
        window.PreviewKeyDown += OnKey;
        window.Deactivated += OnDeactivated;
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not Popup popup)
        {
            return;
        }

        _ = Showing.Remove(popup);

        if (Showing.Count == 0 && Host(popup) is { } window)
        {
            Detach(window);
        }
    }

    private static void Detach(Window window)
    {
        window.PreviewMouseDown -= OnPressed;
        window.PreviewKeyDown -= OnKey;
        window.Deactivated -= OnDeactivated;
    }

    private static void OnPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        foreach (Popup popup in Showing.ToArray())
        {
            // Inside the flyout, including inside any drop-down it opened.
            if (Within(source, popup))
            {
                continue;
            }
            if (Within(source, popup.PlacementTarget))
            {
                continue;
            }

            popup.IsOpen = false;
        }
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        Close();
    }

    private static void OnDeactivated(object? sender, EventArgs e) => Close();

    private static void Close()
    {
        foreach (Popup popup in Showing.ToArray())
        {
            popup.IsOpen = false;
        }
    }

    private static bool Within(DependencyObject source, DependencyObject? target)
    {
        if (target is null)
        {
            return false;
        }

        DependencyObject? node = source;

        while (node is not null)
        {
            if (ReferenceEquals(node, target))
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private static Window? Host(Popup popup) =>
        popup.PlacementTarget is not null ? Window.GetWindow(popup.PlacementTarget) : null;
}
