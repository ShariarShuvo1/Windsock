using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windsock.Core.Meter;
using Windsock.Core.Settings;

namespace Windsock.App.Services;

/// <summary>What a keyboard shortcut does.</summary>
public enum HotkeyAction
{
    /// <summary>Brings the window to the front.</summary>
    ShowWindow,

    /// <summary>Takes the meter off the taskbar, or puts it back.</summary>
    ToggleMeter,

    /// <summary>Pauses the network chart, or resumes it.</summary>
    PauseChart,
}

/// <summary>
/// Keyboard shortcuts that work from anywhere in Windows, through
/// RegisterHotKey on a hidden window of the app's own.
/// </summary>
/// <remarks>
/// Nothing is registered until the person sets a shortcut: one taken by
/// default is one some other program finds already gone. A shortcut another
/// program holds is not taken from it - Windows refuses, and the page says
/// which one and why. MOD_NOREPEAT keeps a held key from firing over and over.
/// </remarks>
public sealed partial class HotkeyService : IDisposable
{
    private const int HotkeyMessage = 0x0312;
    private const uint NoRepeat = 0x4000;
    private const int AlreadyTaken = 1409;

    private readonly ILogger<HotkeyService> _logger;
    private readonly Dictionary<HotkeyAction, string> _problems = [];
    private readonly HashSet<HotkeyAction> _registered = [];
    private HwndSource? _source;
    private MeterShortcuts _applied = new();
    private bool _disposed;

    public HotkeyService(ILogger<HotkeyService> logger) => _logger = logger;

    /// <summary>Raised when a shortcut is pressed.</summary>
    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Raised when which shortcuts are working changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Why a shortcut is not working, for each one that is not.</summary>
    public IReadOnlyDictionary<HotkeyAction, string> Problems => _problems;

    /// <summary>Registers these shortcuts in place of whatever was registered.</summary>
    public void Apply(MeterShortcuts shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);

        if (_disposed)
        {
            return;
        }

        if (shortcuts.ShowWindow == _applied.ShowWindow
            && shortcuts.ToggleMeter == _applied.ToggleMeter
            && shortcuts.PauseChart == _applied.PauseChart
            && (_source is not null || !shortcuts.Any))
        {
            return;
        }

        _applied = shortcuts.Copy();
        Release();
        _problems.Clear();

        if (shortcuts.Any)
        {
            Attach();
            Take(HotkeyAction.ShowWindow, shortcuts.ShowWindow);
            Take(HotkeyAction.ToggleMeter, shortcuts.ToggleMeter);
            Take(HotkeyAction.PauseChart, shortcuts.PauseChart);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();

        if (_source is not null)
        {
            _source.RemoveHook(OnMessage);
            _source.Dispose();
            _source = null;
        }
    }

    private void Take(HotkeyAction action, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        (HotkeyChord? chord, string? problem) = Hotkeys.Parse(text);

        if (chord is not { } wanted)
        {
            _problems[action] = problem ?? "That is not a shortcut Windsock can use.";
            return;
        }

        if (_source is null)
        {
            return;
        }

        bool taken = PInvoke.RegisterHotKey(
            new HWND(_source.Handle),
            (int)action + 1,
            (HOT_KEY_MODIFIERS)((uint)wanted.Modifiers | NoRepeat),
            (uint)wanted.Key);

        if (taken)
        {
            _registered.Add(action);
            return;
        }

        int error = Marshal.GetLastPInvokeError();
        string named = wanted.ToString();

        _problems[action] = error == AlreadyTaken
            ? string.Concat(named, " is already used by another program, or by another of these shortcuts.")
            : string.Concat("Windows would not take ", named, ".");

        LogRefused(named, error);
    }

    private void Release()
    {
        if (_source is null)
        {
            _registered.Clear();
            return;
        }

        foreach (HotkeyAction action in _registered)
        {
            _ = PInvoke.UnregisterHotKey(new HWND(_source.Handle), (int)action + 1);
        }

        _registered.Clear();
    }

    private void Attach()
    {
        if (_source is not null)
        {
            return;
        }

        HwndSourceParameters parameters = new("Windsock shortcuts")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(OnMessage);
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != HotkeyMessage)
        {
            return 0;
        }

        int id = (int)wParam - 1;

        if (Enum.IsDefined((HotkeyAction)id))
        {
            handled = true;
            Pressed?.Invoke(this, (HotkeyAction)id);
        }

        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The shortcut {Chord} could not be registered (error {Error})")]
    private partial void LogRefused(string chord, int error);
}
