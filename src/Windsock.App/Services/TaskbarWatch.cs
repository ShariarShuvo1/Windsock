using System.Runtime.InteropServices;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Windsock.App.Services;

/// <summary>
/// Hears about everything that can move a taskbar, so the meter follows at
/// once rather than at its next patrol.
/// </summary>
/// <remarks>
/// Two ears. A hidden top-level window hears the broadcasts: Explorer
/// restarting (TaskbarCreated, which Windows 10 and later also send when the
/// main display's DPI changes), displays coming and going, the work area
/// changing, and the colour scheme. A "TraySettings" setting change is often
/// said to announce a taskbar moving, but nothing documents Explorer sending
/// it, so it is not relied on; the patrol catches what no broadcast says. The meter's
/// windows cannot hear these themselves, because a child window is never sent
/// a broadcast. And a WinEvent hook on the thread Explorer draws its taskbars
/// on hears each of them move, which is every step of an auto-hiding bar's
/// slide; out of context, so nothing is loaded into Explorer and the calls
/// arrive on this thread's message loop.
/// </remarks>
internal sealed unsafe class TaskbarWatch : IDisposable
{
    private const int DisplayChange = 0x007E;
    private const int SettingChange = 0x001A;
    private const int WorkAreaChanged = 0x002F;
    private const uint AllowMessage = 1;
    private const uint LocationChange = 0x800B;
    private const uint ForegroundChange = 0x0003;
    private const uint OutOfContext = 0x0000;
    private const uint SkipOwnProcess = 0x0002;
    private const int WholeWindow = 0;

    private readonly uint _taskbarCreated;
    private readonly WINEVENTPROC _moved;
    private readonly WINEVENTPROC _front;
    private HwndSource? _source;
    private HWINEVENTHOOK _movedHook;
    private HWINEVENTHOOK _frontHook;
    private uint _watchedThread;
    private bool _disposed;

    public TaskbarWatch()
    {
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");

        // Kept in fields: the hook holds only a pointer, and a delegate the
        // collector has taken would be called anyway.
        _moved = OnMoved;
        _front = OnFront;
    }

    /// <summary>Raised when the set of taskbars or monitors may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when Windows' colour scheme changes, which decides the meter's ink.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>Raised on the UI thread when a top-level window on the taskbar thread moves.</summary>
    public event EventHandler<nint>? Moved;

    /// <summary>Raised when another window comes to the front.</summary>
    public event EventHandler? FrontChanged;

    /// <summary>Starts listening, if it is not already.</summary>
    public void Start()
    {
        if (_disposed || _source is not null)
        {
            return;
        }

        HwndSourceParameters parameters = new("Windsock taskbar watch")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(OnMessage);

        // Elevated, Windsock sits above Explorer, and Windows drops messages
        // sent upward across that line unless they are let through.
        HWND handle = new(_source.Handle);
        _ = PInvoke.ChangeWindowMessageFilterEx(handle, _taskbarCreated, (WINDOW_MESSAGE_FILTER_ACTION)AllowMessage, null);
        _ = PInvoke.ChangeWindowMessageFilterEx(handle, SettingChange, (WINDOW_MESSAGE_FILTER_ACTION)AllowMessage, null);
        _ = PInvoke.ChangeWindowMessageFilterEx(handle, DisplayChange, (WINDOW_MESSAGE_FILTER_ACTION)AllowMessage, null);

        _frontHook = PInvoke.SetWinEventHook(
            ForegroundChange,
            ForegroundChange,
            HMODULE.Null,
            _front,
            0,
            0,
            OutOfContext | SkipOwnProcess);
    }

    /// <summary>
    /// Watches the thread a taskbar is drawn on for its windows moving. The
    /// main and every other taskbar share one thread, so one hook covers all.
    /// </summary>
    public void Follow(uint thread)
    {
        if (_disposed || thread == _watchedThread)
        {
            return;
        }

        Unhook(ref _movedHook);
        _watchedThread = thread;

        if (thread == 0)
        {
            return;
        }

        _movedHook = PInvoke.SetWinEventHook(
            LocationChange,
            LocationChange,
            HMODULE.Null,
            _moved,
            0,
            thread,
            OutOfContext | SkipOwnProcess);
    }

    /// <summary>Stops listening; <see cref="Start"/> starts again.</summary>
    public void Stop()
    {
        Unhook(ref _movedHook);
        Unhook(ref _frontHook);
        _watchedThread = 0;

        if (_source is not null)
        {
            _source.RemoveHook(OnMessage);
            _source.Dispose();
            _source = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    private static void Unhook(ref HWINEVENTHOOK hook)
    {
        if (!hook.IsNull)
        {
            _ = PInvoke.UnhookWinEvent(hook);
            hook = default;
        }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == (int)_taskbarCreated || message == DisplayChange)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return 0;
        }

        if (message != SettingChange)
        {
            return 0;
        }

        string? area = lParam != 0 ? Marshal.PtrToStringUni(lParam) : null;

        if (area == "ImmersiveColorSet")
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (wParam == WorkAreaChanged || area == "Policy")
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    private void OnMoved(
        HWINEVENTHOOK hook,
        uint winEvent,
        HWND window,
        int objectId,
        int childId,
        uint thread,
        uint time)
    {
        // The cursor and the caret report here too, with no window; only a
        // window moving as a whole is a taskbar moving.
        if (objectId == WholeWindow && childId == 0 && window != HWND.Null)
        {
            Moved?.Invoke(this, window);
        }
    }

    private void OnFront(
        HWINEVENTHOOK hook,
        uint winEvent,
        HWND window,
        int objectId,
        int childId,
        uint thread,
        uint time) => FrontChanged?.Invoke(this, EventArgs.Empty);
}
