using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Windsock.App.Controls;

/// <summary>
/// Makes a button in a small menu close the menu's popup when it is pressed,
/// by the mouse, the keyboard or a screen reader alike.
/// </summary>
/// <remarks>
/// <para>
/// A popup with <c>StaysOpen="False"</c> closes on a click elsewhere, which
/// only a mouse can make: a keyboard or screen-reader user who invoked an item
/// was left with the menu open, and while it stayed open Windows' own
/// automation could not close any Windsock window (found in the live check of
/// 2026-10-05). Set by <c>MenuItemStyle</c>, so every menu built from it -
/// the row menus, the folded sidebar's flyout - closes on its own.
/// </para>
/// <para>
/// The popup closes on the button's Click, which comes before its command, so
/// a command that opens a dialog does so with the menu already gone. Focus goes
/// back to what opened the menu only when the item was pressed with a key and
/// the keyboard was inside the menu. Clicked with the mouse, or invoked by a
/// screen reader through UI Automation, focus is left alone: sent back to a
/// row's menu button inside the Apps table, it held the table's order as if
/// the reader were working in it, and focusing brings the button into view,
/// which could scroll the page under the pointer.
/// </para>
/// </remarks>
public static class MenuClosing
{
    public static readonly DependencyProperty ClosesMenuProperty =
        DependencyProperty.RegisterAttached(
            "ClosesMenu",
            typeof(bool),
            typeof(MenuClosing),
            new PropertyMetadata(false, OnClosesMenuChanged));

    public static bool GetClosesMenu(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ClosesMenuProperty);
    }

    public static void SetClosesMenu(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ClosesMenuProperty, value);
    }

    private static void OnClosesMenuChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ButtonBase button)
        {
            return;
        }

        button.Click -= OnClick;

        if (e.NewValue is true)
        {
            KeyEvents.Listen();
            button.Click += OnClick;
        }
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject button || PopupOf(button) is not { IsOpen: true } popup)
        {
            return;
        }

        bool keyboardInside = KeyEvents.InProgress && popup.Child is { IsKeyboardFocusWithin: true };
        popup.IsOpen = false;

        if (keyboardInside && popup.PlacementTarget is UIElement { Focusable: true, IsVisible: true } opener)
        {
            Keyboard.Focus(opener);
        }
    }

    /// <summary>
    /// The popup a menu item sits in. A popup's content is its logical child but
    /// a visual tree of its own, so the logical parent is tried first.
    /// </summary>
    private static Popup? PopupOf(DependencyObject? node)
    {
        while (node is not null and not Popup)
        {
            node = LogicalTreeHelper.GetParent(node)
                ?? (node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null);
        }

        return node as Popup;
    }

    /// <summary>
    /// Whether a key's own event is being handled on this thread right now,
    /// which is how a button's Click tells Enter or Space on it from a mouse
    /// click and from an invoke through UI Automation (queued by the
    /// dispatcher, outside any key).
    /// </summary>
    /// <remarks>
    /// WPF raises a key's routed events between the input manager's pre- and
    /// post-notify for each, and a button clicks inside its own KeyDown
    /// (Enter) or KeyUp (Space), so a count of key events under way answers
    /// it - including Space, which <see cref="KeyPress"/> (key down only,
    /// dropped at the next input) does not see. The most recent device must be
    /// the keyboard too, so a click in a dialog a key handler opened (a nested
    /// message loop, its key still under way) is a click.
    /// </remarks>
    private static class KeyEvents
    {
        [ThreadStatic]
        private static bool t_listening;

        [ThreadStatic]
        private static int t_under;

        /// <summary>Whether a key's event is being handled on this thread.</summary>
        internal static bool InProgress =>
            t_under > 0 && InputManager.Current.MostRecentInputDevice is KeyboardDevice;

        /// <summary>Starts counting this thread's key events; does nothing the second time.</summary>
        internal static void Listen()
        {
            if (t_listening)
            {
                return;
            }

            t_listening = true;
            InputManager.Current.PreNotifyInput += OnPreNotify;
            InputManager.Current.PostNotifyInput += OnPostNotify;
        }

        private static void OnPreNotify(object sender, NotifyInputEventArgs e)
        {
            if (e.StagingItem.Input is KeyEventArgs)
            {
                t_under++;
            }
        }

        private static void OnPostNotify(object sender, NotifyInputEventArgs e)
        {
            if (e.StagingItem.Input is KeyEventArgs && t_under > 0)
            {
                t_under--;
            }
        }
    }
}
