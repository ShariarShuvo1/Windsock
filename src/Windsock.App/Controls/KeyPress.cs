using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Windsock.App.Controls;

/// <summary>
/// Whether a key press is being handled right now: what tells a focus change
/// the keyboard caused from one that only happened.
/// </summary>
/// <remarks>
/// <para>
/// Focus also arrives with no key pressed and no mouse button down: WPF gives
/// it back to the element that had it when the window is activated again, and
/// a popup gives it back when it closes. Deciding "the keyboard did this" by
/// the mouse buttons being up read those as the keyboard, so a table a mouse
/// user had clicked froze its order after an app switch, and the map panned
/// back to a node the user had dragged away from.
/// </para>
/// <para>
/// One class handler on every window's PreviewKeyDown (handled presses too,
/// and keys pressed inside a popup, which route through the window that owns
/// it) raises the flag; it drops at <see cref="DispatcherPriority.Input"/>,
/// after the press and the focus move it causes have been handled. A harness
/// that raises the key events itself goes through the same handler.
/// </para>
/// </remarks>
public static class KeyPress
{
    private static bool _watching;
    private static bool _pressing;

    /// <summary>Whether a key press is being handled at this moment.</summary>
    public static bool IsBeingHandled => _pressing;

    /// <summary>Starts listening, once; call before the first key that matters.</summary>
    public static void Watch()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnKey),
            handledEventsToo: true);
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (_pressing || sender is not DispatcherObject owner)
        {
            return;
        }

        _pressing = true;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _pressing = false));
    }
}
