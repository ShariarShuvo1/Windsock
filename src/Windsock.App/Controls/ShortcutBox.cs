using System.Windows.Controls;
using System.Windows.Input;
using Windsock.Core.Meter;

namespace Windsock.App.Controls;

/// <summary>
/// A box that takes a keyboard shortcut by having it pressed: Ctrl+Alt+W is
/// written as Ctrl+Alt+W, and nothing typed goes in as text.
/// </summary>
/// <remarks>
/// A modifier on its own is waited on rather than taken, so the box fills
/// only once a real key joins it. Backspace or Delete with no modifier
/// empties it, which is how a shortcut is taken away. Tab still moves on, so
/// the keyboard is never trapped in it; that leaves Tab itself, and Escape,
/// which closes things, out of what can be set here.
/// </remarks>
public sealed class ShortcutBox : TextBox
{
    public ShortcutBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = true;
        IsUndoEnabled = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (key is Key.Tab or Key.Escape)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        ModifierKeys held = Keyboard.Modifiers;

        if (key is Key.Back or Key.Delete && held == ModifierKeys.None)
        {
            Text = string.Empty;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;

        if (held.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (held.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (held.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (held.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        // Written whatever it is: a chord Windsock cannot use is shown with the
        // reason beside it, which says more than a box that ignored the keys.
        Text = Hotkeys.Format(new HotkeyChord(modifiers, KeyInterop.VirtualKeyFromKey(key)));
    }
}
