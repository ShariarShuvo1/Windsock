using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Windsock.App.Controls;

/// <summary>
/// Dismisses a popup on a click outside it, without letting it take the mouse;
/// and takes keyboard focus into a popup as it opens and back out as it closes.
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

    /// <summary>
    /// Whether opening this popup from the keyboard puts keyboard focus on the
    /// first thing in it, keeps Tab and Ctrl+Tab going round inside it, closes
    /// it on Escape, and gives focus back to whatever opened it when it closes
    /// with focus inside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A popup is a window of its own, so its content is never in the page's
    /// Tab order: opened with Space, focus stayed on the toggle and Tab went
    /// on through the page, and nothing in the popup could be reached without
    /// the mouse. Moving focus in does not activate the popup's window (WPF
    /// creates it not to be activated) and does not touch the mouse capture a
    /// <c>StaysOpen="False"</c> popup holds, so neither kind closes for it.
    /// </para>
    /// <para>
    /// Opened with the mouse, a pen or a finger, nothing here happens: focus
    /// stays where it was going in and coming out, exactly as before. The
    /// first cut moved it either way, and a mouse user then met a caret in the
    /// first field, the Apps table held its order because focus went back to a
    /// row's menu button, and giving focus back scrolled the page under the
    /// pointer (focusing brings into view) so the button released missed.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty MovesFocusProperty =
        DependencyProperty.RegisterAttached(
            "MovesFocus",
            typeof(bool),
            typeof(Flyout),
            new PropertyMetadata(false, OnMovesFocusChanged));

    public static bool GetMovesFocus(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (bool)element.GetValue(MovesFocusProperty);
    }

    public static void SetMovesFocus(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(MovesFocusProperty, value);
    }

    /// <summary>Whether the popup, now open, was opened from the keyboard.</summary>
    private static readonly DependencyProperty OpenedByKeysProperty =
        DependencyProperty.RegisterAttached(
            "OpenedByKeys",
            typeof(bool),
            typeof(Flyout),
            new PropertyMetadata(false));

    private static void OnMovesFocusChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Popup popup)
        {
            return;
        }

        popup.Opened -= OnFocusOpened;
        popup.Closed -= OnFocusClosed;

        if (e.NewValue is true)
        {
            popup.Opened += OnFocusOpened;
            popup.Closed += OnFocusClosed;
        }
    }

    private static void OnFocusOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup { Child: { } child } popup)
        {
            return;
        }

        KeyboardNavigation.SetTabNavigation(child, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(child, KeyboardNavigationMode.Cycle);
        child.RemoveHandler(Keyboard.KeyDownEvent, (KeyEventHandler)OnFocusKey);
        child.AddHandler(Keyboard.KeyDownEvent, (KeyEventHandler)OnFocusKey);

        bool byKeys = !ByPointer();
        popup.SetValue(OpenedByKeysProperty, byKeys);

        if (!byKeys)
        {
            return;
        }

        // After layout, so content a template has only just built is there to take it.
        _ = popup.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Enter(popup));
    }

    /// <summary>
    /// Whether the input that opened the popup came from the mouse, a pen or
    /// a finger. Anything else - a key, or nothing yet, as on a window that has
    /// had no input at all - counts as the keyboard, the way
    /// <see cref="KeyboardNavigation"/> decides whether to draw focus rings.
    /// </summary>
    private static bool ByPointer() =>
        InputManager.Current.MostRecentInputDevice is MouseDevice or StylusDevice or TouchDevice;

    private static void Enter(Popup popup)
    {
        if (!popup.IsOpen || popup.Child is not { } child)
        {
            return;
        }

        if (Keyboard.FocusedElement is DependencyObject focused && Inside(focused, popup))
        {
            return;
        }

        if (!child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)) && child.Focusable)
        {
            _ = child.Focus();
        }
    }

    private static void OnFocusKey(object sender, KeyEventArgs e)
    {
        // A drop-down inside the popup takes its own keys and marks them handled.
        if (e.Handled || sender is not UIElement child)
        {
            return;
        }

        // Ctrl+Tab goes round the popup too: left to bubble, it reached the
        // sidebar, which turned the page and so closed the popup under it.
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            FocusNavigationDirection way = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                ? FocusNavigationDirection.Previous
                : FocusNavigationDirection.Next;
            UIElement from = Keyboard.FocusedElement is UIElement focused && Within(focused, child) ? focused : child;

            _ = from.MoveFocus(new TraversalRequest(way));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && LogicalTreeHelper.GetParent(child) is Popup { IsOpen: true } popup)
        {
            popup.IsOpen = false;
            e.Handled = true;
        }
    }

    private static void OnFocusClosed(object? sender, EventArgs e)
    {
        if (sender is not Popup popup)
        {
            return;
        }

        bool byKeys = (bool)popup.GetValue(OpenedByKeysProperty);
        popup.ClearValue(OpenedByKeysProperty);

        // Opened by a pointer, focus was never moved, so it is not moved back.
        if (!byKeys)
        {
            return;
        }

        // Focus left on the closed popup's content would be on nothing anyone can see.
        bool inside = Keyboard.FocusedElement is not DependencyObject focused || Inside(focused, popup);

        if (!inside
            || Opener(popup) is not { IsVisible: true, IsEnabled: true, Focusable: true } opener
            || PresentationSource.FromVisual(opener) is null)
        {
            return;
        }

        // An inactive window is not made active for it: it gets the focus back when it is.
        if (Window.GetWindow(opener) is { IsActive: false })
        {
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(opener), opener);
            return;
        }

        _ = opener.Focus();
    }

    /// <summary>The popup's placement target, else the toggle or button it sits inside.</summary>
    private static UIElement? Opener(Popup popup)
    {
        if (popup.PlacementTarget is { } target)
        {
            return target;
        }

        DependencyObject? node = popup;

        while (node is not null)
        {
            if (node is ButtonBase button)
            {
                return button;
            }

            node = node is Visual ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

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

        // Focus inside a popup that moves it: the popup closes itself on Escape,
        // after any drop-down inside it has had the key to close only itself.
        if (Keyboard.FocusedElement is DependencyObject focused
            && Showing.Exists(popup => GetMovesFocus(popup) && Inside(focused, popup)))
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

    /// <summary>Whether an element is the popup's content, or inside a drop-down that content opened.</summary>
    private static bool Inside(DependencyObject element, Popup popup) =>
        (popup.Child is { } child && Within(element, child)) || Within(element, popup);

    private static Window? Host(Popup popup) =>
        popup.PlacementTarget is not null ? Window.GetWindow(popup.PlacementTarget) : null;
}
