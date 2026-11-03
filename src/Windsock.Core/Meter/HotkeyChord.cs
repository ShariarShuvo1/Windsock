using System.Globalization;
using System.Text;

namespace Windsock.Core.Meter;

/// <summary>The modifier keys of a shortcut, with RegisterHotKey's values.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,

    /// <summary>MOD_ALT.</summary>
    Alt = 0x1,

    /// <summary>MOD_CONTROL.</summary>
    Control = 0x2,

    /// <summary>MOD_SHIFT.</summary>
    Shift = 0x4,

    /// <summary>MOD_WIN.</summary>
    Windows = 0x8,
}

/// <summary>A keyboard shortcut: modifiers and one key.</summary>
/// <param name="Modifiers">The modifiers held.</param>
/// <param name="Key">The key's virtual-key code.</param>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, int Key)
{
    /// <summary>The shortcut as a person reads it: "Ctrl+Alt+W".</summary>
    public override string ToString() => Hotkeys.Format(this);
}

/// <summary>
/// Reads and writes keyboard shortcuts, and decides which ones Windsock may
/// take.
/// </summary>
/// <remarks>
/// Rules, from RegisterHotKey's documentation: shortcuts with the Windows key
/// are kept for Windows itself, and F12 for debuggers. A shortcut with only
/// Shift, or none, would take a key away from typing, so Ctrl or Alt is always
/// part of it. A shortcut registered this way is taken from every program, so
/// the ones Windows and every editor answer to are refused too: Alt+F4 and
/// Alt+Space (closing a window, its menu), Ctrl+Alt+Delete, and copy, cut,
/// paste, undo, redo and select all with Ctrl or Ctrl+Shift. A letter needs
/// Shift as well as Ctrl or Alt: Ctrl or Alt and a letter is every program's
/// own (Ctrl+S, Ctrl+F, Alt+F for a File menu), and Ctrl+Alt and a letter is
/// AltGr on many keyboards, which types a character. Digits, F-keys and the
/// named keys need only Ctrl or Alt. Keys are virtual-key codes (winuser.h).
/// </remarks>
public static class Hotkeys
{
    private const int F12 = 0x7B;
    private const int F4 = 0x73;
    private const int Space = 0x20;
    private const int Insert = 0x2D;
    private const int Delete = 0x2E;

    private static readonly (string Name, int Key)[] Named =
    [
        ("Space", 0x20),
        ("PageUp", 0x21),
        ("PageDown", 0x22),
        ("End", 0x23),
        ("Home", 0x24),
        ("Left", 0x25),
        ("Up", 0x26),
        ("Right", 0x27),
        ("Down", 0x28),
        ("Insert", 0x2D),
        ("Delete", 0x2E),
        ("Pause", 0x13),
    ];

    /// <summary>
    /// Reads a shortcut written as "Ctrl+Alt+W", or says why it cannot be one.
    /// Case and spaces do not matter; Control may be written out.
    /// </summary>
    public static (HotkeyChord? Chord, string? Problem) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, "No shortcut.");
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        int? key = null;

        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();

            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    continue;

                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;

                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;

                case "WIN" or "WINDOWS":
                    modifiers |= HotkeyModifiers.Windows;
                    continue;
            }

            if (key is not null || KeyOf(part) is not { } found)
            {
                return (null, string.Concat("\"", text.Trim(), "\" is not a shortcut Windsock can use."));
            }

            key = found;
        }

        if (key is not { } chosen)
        {
            return (null, "A shortcut needs a key as well as Ctrl or Alt.");
        }

        HotkeyChord chord = new(modifiers, chosen);
        return Problem(chord) is { } problem ? (null, problem) : (chord, null);
    }

    /// <summary>Why a shortcut may not be taken, or nothing when it may.</summary>
    public static string? Problem(HotkeyChord chord)
    {
        if ((chord.Modifiers & HotkeyModifiers.Windows) != 0)
        {
            return "Shortcuts with the Windows key belong to Windows.";
        }

        if ((chord.Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt)) == 0)
        {
            return "A shortcut needs Ctrl or Alt, or it would take a key away from typing.";
        }

        if (chord.Key == F12)
        {
            return "F12 is kept for debuggers.";
        }

        if (NameOf(chord.Key) is null)
        {
            return "That key cannot be part of a shortcut.";
        }

        if ((chord.Modifiers == HotkeyModifiers.Alt && chord.Key is F4 or Space)
            || (chord.Modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt) && chord.Key == Delete))
        {
            return string.Concat(Format(chord), " belongs to Windows.");
        }

        bool editing = (chord.Modifiers is HotkeyModifiers.Control or (HotkeyModifiers.Control | HotkeyModifiers.Shift)
                && chord.Key is 'C' or 'V' or 'X' or 'Z' or 'Y' or 'A')
            || (chord.Modifiers == HotkeyModifiers.Control && chord.Key == Insert);

        if (editing)
        {
            return string.Concat(Format(chord), " is how every program copies, pastes or undoes; taking it would take it from them all.");
        }

        return chord.Key is >= 'A' and <= 'Z' && (chord.Modifiers & HotkeyModifiers.Shift) == 0
            ? "With a letter, a shortcut needs Shift as well: Ctrl or Alt and a letter is every program's own, and Ctrl+Alt and a letter types a character on many keyboards."
            : null;
    }

    /// <summary>A shortcut as a person reads it, modifiers in Windows' own order.</summary>
    public static string Format(HotkeyChord chord)
    {
        StringBuilder text = new();

        if ((chord.Modifiers & HotkeyModifiers.Control) != 0)
        {
            text.Append("Ctrl+");
        }

        if ((chord.Modifiers & HotkeyModifiers.Alt) != 0)
        {
            text.Append("Alt+");
        }

        if ((chord.Modifiers & HotkeyModifiers.Shift) != 0)
        {
            text.Append("Shift+");
        }

        if ((chord.Modifiers & HotkeyModifiers.Windows) != 0)
        {
            text.Append("Win+");
        }

        text.Append(NameOf(chord.Key) ?? string.Create(CultureInfo.InvariantCulture, $"Key{chord.Key}"));
        return text.ToString();
    }

    /// <summary>A key's name, or nothing for a key a shortcut may not use.</summary>
    public static string? NameOf(int key)
    {
        if (key is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)key).ToString();
        }

        if (key is >= 0x70 and <= 0x87)
        {
            return string.Create(CultureInfo.InvariantCulture, $"F{key - 0x6F}");
        }

        foreach ((string name, int code) in Named)
        {
            if (code == key)
            {
                return name;
            }
        }

        return null;
    }

    private static int? KeyOf(string name)
    {
        if (name.Length == 1 && char.ToUpperInvariant(name[0]) is var letter and (>= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            return letter;
        }

        if (name.Length is 2 or 3 && name[0] is 'F' or 'f'
            && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number is >= 1 and <= 24)
        {
            return 0x6F + number;
        }

        foreach ((string named, int code) in Named)
        {
            if (string.Equals(named, name, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }

        return null;
    }
}
