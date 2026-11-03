using Windsock.Core.Meter;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Keyboard shortcuts, read and written, and the ones Windsock may take.
/// Virtual-key codes and modifier values from winuser.h.
/// </summary>
public sealed class HotkeysTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Shift+W", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x57)]
    [InlineData("ctrl + shift + f9", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x78)]
    [InlineData("Control+Alt+Home", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x24)]
    [InlineData("Alt+1", HotkeyModifiers.Alt, 0x31)]
    [InlineData("Ctrl+Alt+F24", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x87)]
    [InlineData("Ctrl+Space", HotkeyModifiers.Control, 0x20)]
    public void AShortcut_IsReadAsWritten(string text, HotkeyModifiers modifiers, int key)
    {
        (HotkeyChord? chord, string? problem) = Hotkeys.Parse(text);

        Assert.Null(problem);
        Assert.Equal(new HotkeyChord(modifiers, key), chord);
    }

    [Theory]
    [InlineData("alt+shift+ctrl+w", "Ctrl+Alt+Shift+W")]
    [InlineData("SHIFT+CTRL+PAGEDOWN", "Ctrl+Shift+PageDown")]
    [InlineData("Ctrl+Alt+f1", "Ctrl+Alt+F1")]
    public void AShortcut_IsWrittenInWindowsOrder(string text, string written)
    {
        Assert.Equal(written, Hotkeys.Parse(text).Chord!.Value.ToString());
    }

    [Theory]
    [InlineData("Win+W", "Windows key")]
    [InlineData("Ctrl+Win+W", "Windows key")]
    [InlineData("Shift+W", "Ctrl or Alt")]
    [InlineData("W", "Ctrl or Alt")]
    [InlineData("Ctrl+F12", "debuggers")]
    [InlineData("Ctrl+Alt", "needs a key")]
    [InlineData("Ctrl+Alt+W+Q", "not a shortcut")]
    [InlineData("Ctrl+Alt+Banana", "not a shortcut")]
    [InlineData("", "No shortcut")]
    [InlineData(null, "No shortcut")]
    public void AShortcutWindsockMayNotTake_SaysWhy(string? text, string why)
    {
        (HotkeyChord? chord, string? problem) = Hotkeys.Parse(text);

        Assert.Null(chord);
        Assert.Contains(why, problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A shortcut is taken from every program, so the ones Windows and every
    /// editor answer to are not offered: closing a window, its menu,
    /// Ctrl+Alt+Delete, and copy, cut, paste, undo, redo and select all.
    /// </summary>
    [Theory]
    [InlineData("Alt+F4", "belongs to Windows")]
    [InlineData("Alt+Space", "belongs to Windows")]
    [InlineData("Ctrl+Alt+Delete", "belongs to Windows")]
    [InlineData("Ctrl+C", "copies, pastes or undoes")]
    [InlineData("Ctrl+V", "copies, pastes or undoes")]
    [InlineData("Ctrl+X", "copies, pastes or undoes")]
    [InlineData("Ctrl+Z", "copies, pastes or undoes")]
    [InlineData("Ctrl+A", "copies, pastes or undoes")]
    [InlineData("Ctrl+Y", "copies, pastes or undoes")]
    [InlineData("Ctrl+Shift+Z", "copies, pastes or undoes")]
    [InlineData("Ctrl+Shift+V", "copies, pastes or undoes")]
    [InlineData("Ctrl+Insert", "copies, pastes or undoes")]
    public void WhatWindowsAndEveryEditorAnswerTo_IsRefused(string text, string why)
    {
        (HotkeyChord? chord, string? problem) = Hotkeys.Parse(text);

        Assert.Null(chord);
        Assert.Contains(why, problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A letter needs Shift too: Ctrl or Alt and a letter is every program's
    /// own, and Ctrl+Alt and a letter is AltGr on many keyboards.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+S")]
    [InlineData("Ctrl+F")]
    [InlineData("Ctrl+W")]
    [InlineData("Ctrl+T")]
    [InlineData("Ctrl+N")]
    [InlineData("Ctrl+P")]
    [InlineData("Alt+F")]
    [InlineData("Alt+M")]
    [InlineData("Ctrl+Alt+E")]
    [InlineData("Ctrl+Alt+Q")]
    public void ALetterWithoutShift_IsRefused(string text)
    {
        (HotkeyChord? chord, string? problem) = Hotkeys.Parse(text);

        Assert.Null(chord);
        Assert.Contains("needs Shift as well", problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Alt+Shift+M")]
    [InlineData("Ctrl+Alt+Shift+E")]
    [InlineData("Ctrl+1")]
    [InlineData("Alt+F9")]
    [InlineData("Ctrl+Alt+Home")]
    public void ALetterWithShift_OrAnyOtherKey_IsFine(string text)
    {
        Assert.NotNull(Hotkeys.Parse(text).Chord);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Shift+C")]
    [InlineData("Ctrl+Alt+Shift+V")]
    [InlineData("Alt+Shift+F4")]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Shift+Delete")]
    public void TheSameKeysWithOtherModifiers_AreFine(string text)
    {
        Assert.NotNull(Hotkeys.Parse(text).Chord);
    }

    [Fact]
    public void AKeyPressed_IsJudgedTheSameAsOneTyped()
    {
        Assert.Null(Hotkeys.Problem(new HotkeyChord(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'M')));
        Assert.NotNull(Hotkeys.Problem(new HotkeyChord(HotkeyModifiers.Control, 0x5B)));
    }

    [Theory]
    [InlineData(0x41, "A")]
    [InlineData(0x39, "9")]
    [InlineData(0x70, "F1")]
    [InlineData(0x2E, "Delete")]
    [InlineData(0x10, null)]
    [InlineData(0x5B, null)]
    public void AKey_IsNamedOrRefused(int key, string? name)
    {
        Assert.Equal(name, Hotkeys.NameOf(key));
    }
}
